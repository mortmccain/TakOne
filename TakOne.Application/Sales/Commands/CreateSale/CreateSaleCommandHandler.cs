using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Sales.Commands.CreateSale;

/// <summary>
/// Creates a new Draft Sale with the supplied line items.
///
/// NOTE on the class not being static:
///   Earlier versions of this handler were `public static class`. That breaks
///   `ILogger<CreateSaleCommandHandler>` because C# forbids static types as
///   generic type arguments. The class is now `sealed` (instance-able but
///   non-inheritable); the HandleAsync method stays `static`, which is what
///   Wolverine's source-generated discovery actually looks for.
/// </summary>
public sealed class CreateSaleCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync(
        CreateSaleCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IProductRepository productRepository,
        ISaleRepository saleRepository,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        IUnitOfWork unitOfWork,
        ILogger<CreateSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // ------------------------------------------------------------------
        // 1. Defensive auth check. The [RequireRoles] middleware already
        //    rejects unauthenticated callers, but this method may also be
        //    invoked in tests or from a non-HTTP host — re-checking here
        //    keeps the invariant honest.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated)
        {
            return Result<Guid>.Failure("Authentication required.");
        }

        if (currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateSale: authenticated user has an empty UserId. Claims may be misconfigured.");
            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 2. Resolve the customer by worker ID. The employee types the
        //    customer's worker ID — never a Guid — into the sales-employee
        //    page. For self-buy, the worker ID equals the current user's
        //    worker ID, but the lookup still goes through the repository so
        //    the path is identical.
        // ------------------------------------------------------------------
        var customer = await userRepository.GetByWorkerIdAsync(command.CustomerWorkerId, cancellationToken);
        if (customer is null)
        {
            logger.LogWarning(
                "CreateSale: customer with worker ID '{WorkerId}' was not found. Requested by user {UserId}.",
                command.CustomerWorkerId, currentUser.UserId);

            return Result<Guid>.Failure(
                $"No user found with worker ID '{command.CustomerWorkerId}'.");
        }

        if (!customer.IsActive)
        {
            logger.LogWarning(
                "CreateSale: customer '{WorkerId}' is inactive. Requested by user {UserId}.",
                command.CustomerWorkerId, currentUser.UserId);

            return Result<Guid>.Failure(
                $"User '{command.CustomerWorkerId}' is inactive and cannot be the customer of a sale.");
        }

        // ------------------------------------------------------------------
        // 2b. SECURITY: verify the resolved user is actually a Customer,
        //     AND (if the caller is a Customer) that they're not creating a
        //     sale on behalf of ANOTHER customer. Brutal Code Review v3
        //     finding #04: the previous code only checked customer.IsActive
        //     — it did NOT verify the resolved user had the Customer role,
        //     NOR that customer.Id == currentUser.UserId when the caller
        //     was a Customer. Any authenticated user could pass another
        //     user's WorkerId (any role!) and create a sale with that user
        //     as CustomerId — bypassing their own purchase limits, salary
        //     budget, and currency restrictions. An Employee could even
        //     create a sale with an Admin as CustomerId.
        // ------------------------------------------------------------------
        // Fetch the resolved customer's roles. Roles live in ASP.NET
        // Identity's AspNetUserRoles + AspNetRoles (not on the Domain User),
        // so we batch-resolve via the repository's role-lookup method.
        var customerRolesMap = await userRepository.GetRolesByUserIdsAsync(
            new[] { customer.Id }, cancellationToken);

        // A missing key means "no roles" (rare — incomplete role seeding).
        // Treat that as a rejection: a user with no roles is NOT a valid
        // customer for a sale.
        var customerRoles = customerRolesMap.TryGetValue(customer.Id, out var roles)
            ? roles
            : new List<string>();

        var isCustomerRole = customerRoles.Contains(Roles.Customer);

        if (!isCustomerRole)
        {
            logger.LogWarning(
                "CreateSale: resolved user '{WorkerId}' (Id={CustomerId}) is NOT a Customer " +
                "(roles: {Roles}). Sale creation rejected. Requested by user {UserId}.",
                command.CustomerWorkerId, customer.Id, string.Join(", ", customerRoles),
                currentUser.UserId);

            return Result<Guid>.Failure(
                $"User '{command.CustomerWorkerId}' is not a customer and cannot be the customer of a sale.");
        }

        // If the CALLER is a Customer (non-staff), they may only create
        // a sale for THEMSELVES. Staff (Employee/Manager/Admin) may create
        // sales on behalf of any customer. This closes the impersonation
        // hole: a Customer can no longer pass another customer's WorkerId
        // to buy on their behalf (which would bypass the caller's own
        // purchase limits and salary budget).
        var callerIsCustomer = currentUser.IsInRole(Roles.Customer);

        if (callerIsCustomer && customer.Id != currentUser.UserId)
        {
            logger.LogWarning(
                "CreateSale: Customer {CallerId} attempted to create a sale for a DIFFERENT " +
                "customer {TargetId} (WorkerId '{WorkerId}'). Impersonation rejected.",
                currentUser.UserId, customer.Id, command.CustomerWorkerId);

            return Result<Guid>.Failure(
                "Customers can only create sales for themselves. " +
                "Contact a staff member to create a sale on behalf of another customer.");
        }

        // ------------------------------------------------------------------
        // 3. Create the Sale in Draft state (B2 deferred-allocation design).
        //    Drafts are created WITHOUT a SaleNumber — the permanent number
        //    is allocated only when the customer submits (see
        //    SubmitSaleCommandHandler). SaleNumber is passed as null here;
        //    the EF configuration stores it as NULL, and the filtered unique
        //    index on (SaleNumber_Year, SaleNumber_Sequence) allows multiple
        //    concurrent drafts (NULLs are exempt from the filter).
        //    CustomerId/Name come from the resolved customer.
        //    CreatedById/Name come from the current user (which may equal
        //    the customer for self-buy, or be a staff member for on-behalf).
        // ------------------------------------------------------------------
        var sale = Sale.Create(
            customerId: customer.Id,
            customerName: customer.FullName,
            saleNumber: null,
            createdByUserId: currentUser.UserId,
            createdByName: currentUser.FullName);

        // ------------------------------------------------------------------
        // 5. Add each line item. For each item we:
        //      a. Load the product (for name snapshot, price snapshot, stock
        //         check, and purchase-limit lookup).
        //      b. Check stock: the requested quantity must be ≤ current stock.
        //         Stock is NOT decremented here — only at Approve time.
        //      c. Look up the customer's per-group purchase limit using the
        //         CUSTOMER's GroupName (NOT the current user's, in case a
        //         staff member is creating the sale on behalf of a customer).
        //      d. Add the line to the sale. The aggregate enforces the limit.
        //
        //    If any item fails, we return immediately — the in-memory Sale
        //    is discarded (no rollback needed, nothing persisted yet).
        // ------------------------------------------------------------------
        foreach (var item in command.Items)
        {
            var product = await productRepository.GetByIdAsync(item.ProductId, cancellationToken);
            if (product is null)
            {
                return Result<Guid>.Failure(
                    $"Product '{item.ProductId}' was not found.");
            }

            if (item.Quantity > product.StockQuantity)
            {
                return Result<Guid>.Failure(
                    $"Not enough stock for '{product.Name}' " +
                    $"(requested {item.Quantity}, available {product.StockQuantity}).");
            }

            // GetPurchaseLimitForGroup returns the CustomerGroupPurchaseLimit
            // value object (or null if no limit is defined for that group on
            // this product). The Sale aggregate expects the raw int? — so we
            // unwrap .Limit here. A null GroupId (staff buying as themselves
            // for themselves) means no limit applies.
            //
            // Step 12-c runtime fix: route through IPurchaseLimitPolicy so
            // the LimitMode is honored. The policy returns null when
            // LimitMode == SalaryOnly (count limits are off in that mode),
            // so the Sale aggregate's EnsurePurchaseLimitRespected will
            // skip the check. Without this routing, the limit was always
            // resolved from the product regardless of mode, which meant
            // staff creating a sale on behalf of a customer got the count
            // limit enforced even in SalaryOnly mode — the exact bug the
            // user reported (couldn't add more than 1 unit to a cart).
            int? purchaseLimit = await purchaseLimitPolicy.GetCountLimitAsync
                (product.Id, customer.GroupId, cancellationToken);

            // SNAPSHOT THE PRICE — pass a NEW Money instance, NOT product.Price
            // by reference. See CreateOrAppendSaleCommandHandler for the full
            // rationale (EF Core shared-owned-entity tracking issue).
            sale.AddLineItem(
                productId: product.Id,
                productName: product.Name,
                quantity: item.Quantity,
                unitPrice: new Money(product.Price.Amount, product.Price.Currency),
                purchaseLimit: purchaseLimit);
        }

        // ------------------------------------------------------------------
        // 6. Persist. Single transaction — EF Core tracks the Sale and its
        //    line items as one unit.
        // ------------------------------------------------------------------
        await saleRepository.AddAsync(sale, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var isSelfBuy = customer.Id == currentUser.UserId;
        logger.LogInformation(
            "CreateSale: draft {SaleId} created by {CreatorId} for customer {CustomerId}. Self-buy: {SelfBuy}.",
            sale.Id, currentUser.UserId, customer.Id, isSelfBuy);

        return Result<Guid>.Success(sale.Id);
    }
}
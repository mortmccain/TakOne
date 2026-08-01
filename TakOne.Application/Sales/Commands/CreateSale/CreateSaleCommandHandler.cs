using Microsoft.Extensions.Logging;
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
    /// <summary>
    /// Prefix used for the human-readable SaleNumber (e.g. "SALE-2026-0001").
    /// Centralized here so it's easy to change without hunting for string literals.
    /// </summary>
    public const string SaleNumberPrefix = "SALE";

    public static async Task<Result<Guid>> HandleAsync(
        CreateSaleCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IProductRepository productRepository,
        ISaleRepository saleRepository,
        ISaleNumberGenerator saleNumberGenerator,
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
        // 3. Generate the sale number. ISaleNumberGenerator is backed by the
        //    Infrastructure layer (step 7d), which uses PersianCalendar to
        //    compute the Persian year and counts ALL existing sales in that
        //    year to produce a globally-unique sequence.
        //    Concurrency note: two simultaneous CreateSale calls could both
        //    compute the same sequence. The unique index on
        //    (SaleNumber_Year, SaleNumber_Sequence) on the Sales table causes
        //    the loser's SaveChangesAsync to fail. A retry policy (Polly)
        //    will be added in a future hardening pass.
        // ------------------------------------------------------------------
        var saleNumber = await saleNumberGenerator.NextAsync(cancellationToken);
        // ------------------------------------------------------------------
        // 4. Create the Sale in Draft state.
        //    CustomerId/Name come from the resolved customer.
        //    CreatedById/Name come from the current user (which may equal
        //    the customer for self-buy, or be a staff member for on-behalf).
        // ------------------------------------------------------------------
        var sale = Sale.Create(
            customerId: customer.Id,
            customerName: customer.FullName,
            saleNumber: saleNumber,
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
            // unwrap .Limit here. A null GroupName (staff buying as themselves
            // for themselves) means no limit applies.
            int? purchaseLimit = null;
            if (!string.IsNullOrWhiteSpace(customer.GroupName))
            {
                var limitVo = product.GetPurchaseLimitForGroup(customer.GroupName);
                purchaseLimit = limitVo?.Limit;
            }

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
            "CreateSale: sale {SaleId} ({SaleNumber}) created by {CreatorId} for customer {CustomerId}. Self-buy: {SelfBuy}.",
            sale.Id, sale.SaleNumber, currentUser.UserId, customer.Id, isSelfBuy);

        return Result<Guid>.Success(sale.Id);
    }
}
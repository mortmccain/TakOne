using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Sales.Commands.AddItemToSale;

/// <summary>
/// Adds a line item to a Draft Sale. See <see cref="AddItemToSaleCommand"/> for
/// the full business rules. Not static so <c>ILogger&lt;T&gt;</c> can take it
/// as a type argument.
/// </summary>
public sealed class AddItemToSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        AddItemToSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        ISalaryBudgetService salaryBudgetService,
        ICartMutationLock cartMutationLock,
        IUnitOfWork unitOfWork,
        ILogger<AddItemToSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // Load the sale WITH line items, because we need to:
        //   - check ownership
        //   - check status
        //   - find an existing line for this product (for stock aggregation)
        //
        // We do this BEFORE acquiring the cart mutation lock — the load
        // is read-only and we need the Sale.CustomerId (not just the
        // command, which has the SaleId) to know whose lock to acquire.
        // ------------------------------------------------------------------
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // Only the creator can edit a draft. For self-buy, that's the customer.
        // For on-behalf, that's the staff member who started the draft.
        if (sale.CreatedByUserId != currentUser.UserId)
        {
            logger.LogWarning(
                "AddItemToSale: user {UserId} attempted to modify sale {SaleId} owned by {OwnerId}.",
                currentUser.UserId, sale.Id, sale.CreatedByUserId);

            return Result.Failure("You can only modify your own drafts.");
        }

        if (sale.Status != SaleStatus.Draft)
        {
            return Result.Failure(
                $"Items can only be added to a Draft sale. This sale is currently '{sale.Status}'.");
        }

        // ------------------------------------------------------------------
        // ACQUIRE PER-USER CART MUTATION LOCK (Step 4 wiring).
        //
        // The lock is acquired on sale.CustomerId (NOT currentUser.UserId)
        // so that staff-editing-on-behalf-of-customer serializes on the
        // CUSTOMER's lock — two staff members editing the same customer's
        // cart must not race. For self-buy, sale.CustomerId ==
        // currentUser.UserId so it doesn't matter which we use.
        //
        // The lock serializes ALL cart mutations for this customer. The
        // salary budget check (below) reads the customer's "consumed"
        // amount — without the lock, two concurrent invocations could
        // BOTH see the same stale consumed amount and BOTH decide their
        // line fits within the remaining budget, even though together
        // they exceed it.
        //
        // `await using` guarantees release on all exit paths (including
        // exceptions and early returns).
        // ------------------------------------------------------------------
        await using var _cartLockHandle = await cartMutationLock.AcquireAsync(sale.CustomerId, cancellationToken);

        // ------------------------------------------------------------------
        // RE-LOAD THE SALE AFTER ACQUIRING THE LOCK.
        //
        // Between the first load (before the lock) and now, another
        // invocation may have mutated the sale (added a line, etc.).
        // Re-loading gives us the post-lock state so the salary budget
        // check (which depends on sale.Total + sale.LineItems) is accurate.
        //
        // The first load was needed to validate ownership and status
        // BEFORE blocking on the lock — we don't want to hold the lock
        // while checking permissions (the caller may be unauthorized).
        // ------------------------------------------------------------------
        sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null || sale.Status != SaleStatus.Draft)
        {
            // Defensive: the sale was loaded moments ago and passed the
            // status check. If it's now gone or no longer Draft, a
            // concurrent invocation submitted it. Treat as a conflict.
            logger.LogWarning(
                "AddItemToSale: sale {SaleId} changed state after acquiring cart lock (was Draft, now {Status}).",
                command.SaleId, sale?.Status.ToString() ?? "<null>");
            return Result.Failure(CartConflictErrors.Format());
        }

        // ------------------------------------------------------------------
        // Load the product. We need it for:
        //   - name snapshot (added to the line)
        //   - price snapshot (added to the line)
        //   - stock check
        //   - per-group purchase-limit lookup (via policy now, not inline)
        //
        // READ-ONLY LOAD (AsNoTracking): same rationale as
        // CreateOrAppendSaleCommandHandler — we never mutate the Product
        // here, only snapshot its data into the SaleLineItem. Tracking it
        // would add Product.Price#Money to the change tracker alongside
        // the Sale.Total#Money and SaleLineItem.UnitPrice#Money owned
        // instances, which can confuse EF Core and produce
        // DbUpdateConcurrencyException at SaveChanges. AsNoTracking keeps
        // the Product out of the tracking equation entirely.
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdReadOnlyAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // ------------------------------------------------------------------
        // CATEGORY-DEACTIVATION CHECK.
        //   Same rule as CreateOrAppendSaleCommandHandler: if the product's
        //   Category / SubCategory / SubSubCategory has been deactivated,
        //   the product is unbuyable. StockQuantity is preserved. This
        //   path is used by the admin / staff sale-detail page when
        //   building a sale on behalf of a customer — the admin can still
        //   SEE the product on the AdminProducts page (with a
        //   "(deactivated)" badge), but they cannot add it to a sale.
        //
        //   The error uses CategoryDeactivatedErrors.Format so the UI
        //   can localize it.
        // ------------------------------------------------------------------
        var hierarchyActive = await categoryRepository.IsProductCategoryHierarchyActiveAsync
            (
            product.CategoryId,
            product.SubCategoryId,
            product.SubSubCategoryId,
            cancellationToken
            );

        if (!hierarchyActive)
        {
            logger.LogWarning
                ("AddItemToSale: product {ProductId} ('{ProductName}') is under a deactivated category (Category={CategoryId}, Sub={SubCategoryId}, SubSub={SubSubCategoryId}). Rejecting add to sale {SaleId}.",
                product.Id, product.Name, product.CategoryId, product.SubCategoryId, product.SubSubCategoryId, sale.Id);

            return Result.Failure(
                CategoryDeactivatedErrors.Format(product.Name));
        }

        // ------------------------------------------------------------------
        // Stock check: the resulting line quantity (existing + new) must not
        // exceed current stock. If a line for this product already exists on
        // the sale, the aggregate will INCREMENT its quantity — so the final
        // quantity is what matters, not just the amount being added now.
        // ------------------------------------------------------------------
        var existingLineQuantity = sale.LineItems
            .Where(li => li.ProductId == command.ProductId)
            .Sum(li => li.Quantity);

        var resultingQuantity = existingLineQuantity + command.Quantity;

        if (resultingQuantity > product.StockQuantity)
        {
            return Result.Failure(
                $"Adding {command.Quantity} unit(s) of '{product.Name}' would bring the line total to " +
                $"{resultingQuantity}, but only {product.StockQuantity} are currently in stock.");
        }

        // ------------------------------------------------------------------
        // Purchase-limit + currency + salary-budget checks using the CUSTOMER's
        // GroupId (not the current user's, in case a staff member is editing
        // a draft they're building on behalf of a customer). The sale stores
        // CustomerId (a Guid) — we need to re-load the user to get their GroupId.
        //
        // The customer is reloaded AFTER the lock is acquired — so a
        // concurrent group reassignment is observed (the customer's
        // GroupId may have changed since the first load). The policy /
        // budget checks below use the authoritative post-lock value.
        // ------------------------------------------------------------------
        var customer = await userRepository.GetByIdAsync(sale.CustomerId, cancellationToken);
        if (customer is null)
        {
            // Defensive: the customer was loaded when the sale was created, so
            // they existed. They may have been hard-deleted in the meantime
            // (which shouldn't be possible via our domain — users are soft-
            // deleted via Deactivate()). Treat this as a data integrity issue.
            logger.LogError(
                "AddItemToSale: customer {CustomerId} on sale {SaleId} was not found in the user repository.",
                sale.CustomerId, sale.Id);

            return Result.Failure(
                "The customer associated with this sale could not be found. Contact an administrator.");
        }

        // ------------------------------------------------------------------
        // GROUP-MEMBERSHIP GUARD (Step 12-a runtime fix).
        //
        //    Business rule: a customer MUST belong to a CustomerGroup for
        //    any purchase mutation (including staff adding items to a
        //    draft on the customer's behalf). Without a group, the
        //    customer has no salary budget / currency / per-product cap
        //    to enforce — i.e. unlimited mutations, which defeats the
        //    entire salary/budget feature.
        //
        //    The error uses NoCustomerGroupErrors.Format so the UI layer
        //    (SaleDetail page) can localize it without exposing the
        //    internal "customer group" concept to the staff user.
        // ------------------------------------------------------------------
        if (customer.GroupId is null)
        {
            logger.LogWarning
                ("AddItemToSale: customer {CustomerId} on sale {SaleId} has no customer group assigned. " +
                 "Rejecting add-item for product {ProductId} ('{ProductName}').",
                customer.Id, sale.Id, product.Id, product.Name);

            return Result.Failure(NoCustomerGroupErrors.Format());
        }

        // ------------------------------------------------------------------
        // CURRENCY MATCH CHECK (always enforced, regardless of LimitMode).
        // A customer whose salary is in IRR cannot buy a product priced
        // in USD. GroupId is guaranteed non-null here (guarded above).
        // ------------------------------------------------------------------
        if (customer.GroupId is not null)
        {
            var currencyOk = await purchaseLimitPolicy.IsCurrencyMatchAsync
                (product.Id, customer.GroupId, cancellationToken);

            if (!currencyOk)
            {
                // Salary currency resolved via the budget service's cached
                // helper (single round-trip, often IMemoryCache-backed).
                var salary = await salaryBudgetService.GetGroupSalaryAsync
                    (customer.GroupId, cancellationToken);
                var salaryCurrency = salary?.Currency ?? "???";

                logger.LogWarning
                    ("AddItemToSale: currency mismatch for product {ProductId} ('{ProductName}') priced in {ProductCurrency}; customer {CustomerId} salary currency is {SalaryCurrency}. Rejecting add to sale {SaleId}.",
                    product.Id, product.Name, product.Price.Currency, sale.CustomerId, salaryCurrency, sale.Id);

                return Result.Failure(
                    CurrencyMismatchErrors.Format(product.Name, product.Price.Currency, salaryCurrency));
            }
        }

        // ------------------------------------------------------------------
        // SALARY BUDGET CHECK (enforced when LimitMode is SalaryOnly or Both).
        //
        // delta = unitPrice.Amount × quantity — the SIGNED change to the
        // customer's monthly consumed amount. The budget info's Consumed
        // field ALREADY includes the customer's current draft cart total
        // (cart-reserves-budget rule), so the check is simply:
        //
        //   delta ≤ budgetInfo.Remaining
        //
        // (where Remaining = Salary − Consumed; can be negative if the
        // salary was lowered mid-month after a purchase).
        //
        // Skip when customer has no group (staff) — no budget applies.
        // ------------------------------------------------------------------
        var lineTotal = product.Price.Amount * command.Quantity;

        if (customer.GroupId is not null
            && await purchaseLimitPolicy.IsSalaryBudgetEnforcedAsync(cancellationToken))
        {
            var budgetInfo = await salaryBudgetService.GetBudgetInfoAsync
                (sale.CustomerId, cancellationToken);

            if (budgetInfo is not null && lineTotal > budgetInfo.Remaining)
            {
                logger.LogWarning
                    ("AddItemToSale: salary budget exceeded for product {ProductId} on sale {SaleId} (customer {CustomerId}, lineTotal {LineTotal}, remaining budget {Remaining}, currency {Currency}).",
                    product.Id, sale.Id, sale.CustomerId, lineTotal, budgetInfo.Remaining, budgetInfo.Salary.Currency);

                return Result.Failure(
                    SalaryBudgetExceededErrors.Format
                        (product.Name, lineTotal, budgetInfo.Remaining, budgetInfo.Salary.Currency));
            }
        }

        // ------------------------------------------------------------------
        // PER-PRODUCT COUNT LIMIT (replaces inline product.GetPurchaseLimitForGroup).
        // Returns null when:
        //   - count limits are NOT enforced (mode = SalaryOnly)
        //   - customer has no group (staff)
        //   - the product has no limit set for the customer's group
        // ------------------------------------------------------------------
        int? purchaseLimit = null;
        if (customer.GroupId is not null)
        {
            purchaseLimit = await purchaseLimitPolicy.GetCountLimitAsync
                (product.Id, customer.GroupId, cancellationToken);
        }

        // ------------------------------------------------------------------
        // Add the line. The aggregate handles the limit check (throwing
        // DomainException on violation) and either creates a new line or
        // increments an existing one for the same product.
        //
        // We wrap the call in try/catch (DomainException) to return a
        // friendly Result.Failure with a Persian message instead of
        // letting the exception propagate as a generic error toast.
        // Same pattern as UpdateSaleLineItemCommandHandler + the append
        // path of CreateOrAppendSaleCommandHandler.
        //
        // SNAPSHOT THE PRICE — pass a NEW Money instance, NOT product.Price
        // by reference. EF Core tracks owned value objects by reference, so
        // if SaleLineItem.UnitPrice and Product.Price point to the SAME Money
        // object, the change tracker throws DbUpdateConcurrencyException:
        //   "The same entity is being tracked as different entity types
        //    'SaleLineItem.UnitPrice#Money' and 'Product.Price#Money'"
        // See CreateOrAppendSaleCommandHandler for the full rationale.
        // ------------------------------------------------------------------
        try
        {
            sale.AddLineItem(
                productId: product.Id,
                productName: product.Name,
                quantity: command.Quantity,
                unitPrice: new Money(product.Price.Amount, product.Price.Currency),
                purchaseLimit: purchaseLimit);
        }
        catch (DomainException ex) when (ex.Message.Contains("Purchase limit", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning
                ("AddItemToSale: purchase-limit exceeded for product {ProductId} on sale {SaleId} (customer {CustomerId}, limit {Limit}, requested {Qty}). Domain message: {Msg}",
                product.Id, sale.Id, sale.CustomerId, purchaseLimit, command.Quantity, ex.Message);

            // Use the culture-neutral error format. The UI layer
            // (SaleDetail.razor) intercepts this with
            // PurchaseLimitErrors.TryParse and substitutes a localized
            // message that does NOT mention "groups".
            if (purchaseLimit.HasValue)
            {
                return Result.Failure(
                    PurchaseLimitErrors.Format(product.Name, purchaseLimit.Value));
            }

            // Defensive — the catch only fires when purchaseLimit was
            // set, but if it's somehow null, fall back to a generic
            // English message.
            return Result.Failure(
                $"Purchase limit exceeded for '{product.Name}'.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "AddItemToSale: added {Qty} of product {ProductId} to sale {SaleId}. Resulting line total: {Total}.",
            command.Quantity, product.Id, sale.Id, resultingQuantity);

        return Result.Success();
    }
}
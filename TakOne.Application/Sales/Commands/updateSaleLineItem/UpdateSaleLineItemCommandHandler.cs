using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.UpdateSaleLineItem;

/// <summary>
/// Updates the quantity of a line item on a Draft Sale.
///
/// Re-enforces the per-group purchase limit using the line's Product, because
/// the customer's group limit (or the product's limit) may have changed since
/// the line was first added. Uses the CUSTOMER's group, not the current user's,
/// since staff may be editing a draft on behalf of a customer.
/// </summary>
public sealed class UpdateSaleLineItemCommandHandler
{
    public static async Task<Result> HandleAsync(
        UpdateSaleLineItemCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUserRepository userRepository,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        ISalaryBudgetService salaryBudgetService,
        ICartMutationLock cartMutationLock,
        IUnitOfWork unitOfWork,
        ILogger<UpdateSaleLineItemCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        // Need line items eagerly loaded so we can find the line by ID.
        // We do this BEFORE acquiring the lock — the load is read-only
        // and we need sale.CustomerId + the line's ProductId to know whose
        // lock to acquire and which product to enforce policy against.
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        if (sale.CreatedByUserId != currentUser.UserId)
        {
            logger.LogWarning(
                "UpdateSaleLineItem: user {UserId} attempted to modify sale {SaleId} owned by {OwnerId}.",
                currentUser.UserId, sale.Id, sale.CreatedByUserId);

            return Result.Failure("You can only modify your own drafts.");
        }

        if (sale.Status != SaleStatus.Draft)
        {
            return Result.Failure(
                $"Line items can only be updated in a Draft sale. This sale is currently '{sale.Status}'.");
        }

        var lineItem = sale.LineItems.FirstOrDefault(li => li.Id == command.LineItemId);
        if (lineItem is null)
        {
            return Result.Failure(
                $"Line item '{command.LineItemId}' was not found on sale '{sale.SaleNumber}'.");
        }

        // ------------------------------------------------------------------
        // ACQUIRE PER-USER CART MUTATION LOCK (Step 4 wiring).
        //
        // Acquired on sale.CustomerId (NOT currentUser.UserId) so that
        // staff-editing-on-behalf serializes on the customer's lock.
        // `await using` guarantees release on all exit paths.
        // ------------------------------------------------------------------
        await using var _cartLockHandle = await cartMutationLock.AcquireAsync(sale.CustomerId, cancellationToken);

        // Re-load the sale after acquiring the lock — a concurrent
        // invocation may have mutated or removed the line. The first load
        // was needed to validate ownership + find the line BEFORE blocking
        // on the lock; the second load gives us the authoritative post-lock
        // state for the salary budget check (which depends on sale.Total).
        sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null || sale.Status != SaleStatus.Draft)
        {
            logger.LogWarning(
                "UpdateSaleLineItem: sale {SaleId} changed state after acquiring cart lock (was Draft, now {Status}).",
                command.SaleId, sale?.Status.ToString() ?? "<null>");
            return Result.Failure(CartConflictErrors.Format());
        }

        lineItem = sale.LineItems.FirstOrDefault(li => li.Id == command.LineItemId);
        if (lineItem is null)
        {
            // The line was removed by a concurrent invocation between our
            // first load and our second load.
            logger.LogWarning(
                "UpdateSaleLineItem: line {LineId} disappeared from sale {SaleId} between first and second load (likely removed by a concurrent invocation).",
                command.LineItemId, command.SaleId);
            return Result.Failure(
                "This item was removed from your cart by another session. Refresh the page.");
        }

        // ------------------------------------------------------------------
        // Load the product so we can:
        //   - check stock against the new quantity
        //   - re-resolve the per-group purchase limit
        //
        // READ-ONLY load (AsNoTracking): we only READ from the Product here
        // (stock + purchase limit). The mutation goes through the Sale
        // aggregate's UpdateLineItemQuantity, not through the Product.
        // Tracking the Product would put its owned Money Price into the
        // change tracker alongside the Sale's owned Money Total and the
        // SaleLineItem's owned Money UnitPrice, which can trigger
        // DbUpdateConcurrencyException at SaveChanges. See
        // CreateOrAppendSaleCommandHandler for the full rationale.
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdReadOnlyAsync(lineItem.ProductId, cancellationToken);
        if (product is null)
        {
            // Defensive — the product existed when the line was added. If it's
            // gone now, the database is in an inconsistent state.
            logger.LogError(
                "UpdateSaleLineItem: product {ProductId} on sale {SaleId} line {LineId} no longer exists.",
                lineItem.ProductId, sale.Id, lineItem.Id);

            return Result.Failure(
                $"Product '{lineItem.ProductName}' no longer exists. Remove the line instead.");
        }

        // Stock check: the new quantity must not exceed current stock.
        if (command.Quantity > product.StockQuantity)
        {
            return Result.Failure(
                $"Cannot set quantity to {command.Quantity} for '{product.Name}' — " +
                $"only {product.StockQuantity} are in stock.");
        }

        // ------------------------------------------------------------------
        // Resolve the customer (the sale's CustomerId, not the current user)
        // — staff may be editing a draft on behalf of a customer. Loaded
        // AFTER the cart lock is acquired so a concurrent group reassignment
        // is observed.
        // ------------------------------------------------------------------
        var customer = await userRepository.GetByIdAsync(sale.CustomerId, cancellationToken);
        if (customer is null)
        {
            logger.LogError(
                "UpdateSaleLineItem: customer {CustomerId} on sale {SaleId} not found. [{UnexpectedCode}]",
                sale.CustomerId, sale.Id,
                UnexpectedErrorCodes.UpdateSaleLineItem_CustomerDisappeared);

            // Wire-format prefix "UE|" — see ErrorDisplayService.Localize.
            return Result.Failure(
                $"UE|{UnexpectedErrorCodes.UpdateSaleLineItem_CustomerDisappeared}");
        }

        // ------------------------------------------------------------------
        // GROUP-MEMBERSHIP GUARD (Step 12-a runtime fix).
        //
        //    Business rule: a user MUST belong to a CustomerGroup for any
        //    purchase mutation. Without a group, the customer has no
        //    salary budget / currency / per-product cap to enforce — i.e.
        //    unlimited mutations, which defeats the salary/budget feature.
        //
        //    This applies to ALL customers (staff editing on behalf of a
        //    customer included — the customer must have a group). If a
        //    customer has somehow ended up with no group (legacy data,
        //    admin error), they cannot mutate any cart line until they're
        //    assigned to one. This blocks the exact bug reported in the
        //    Step 12-a runtime review: a no-group user could freely
        //    increase line quantities without any limit enforcement.
        //
        //    The error uses NoCustomerGroupErrors.Format so the UI layer
        //    (Cart page) can localize it without exposing the internal
        //    "customer group" concept.
        // ------------------------------------------------------------------
        if (customer.GroupId is null)
        {
            logger.LogWarning
                ("UpdateSaleLineItem: customer {CustomerId} on sale {SaleId} has no customer group assigned. " +
                 "Rejecting line update for product {ProductId} ('{ProductName}').",
                customer.Id, sale.Id, lineItem.ProductId, lineItem.ProductName);

            return Result.Failure(NoCustomerGroupErrors.Format());
        }

        // ------------------------------------------------------------------
        // CURRENCY MATCH CHECK (always enforced, regardless of LimitMode).
        // A customer whose salary is in IRR cannot buy a product priced
        // in USD. GroupId is guaranteed non-null here (guarded above).
        //
        // The price snapshot on the line item is whatever the product's
        // price was WHEN THE LINE WAS ADDED. We use that for the currency
        // comparison (not the product's current price) — the line's
        // currency is what the customer is being charged in.
        // ------------------------------------------------------------------
        if (customer.GroupId is not null)
        {
            var currencyOk = await purchaseLimitPolicy.IsCurrencyMatchAsync
                (product.Id, customer.GroupId, cancellationToken);

            if (!currencyOk)
            {
                var salary = await salaryBudgetService.GetGroupSalaryAsync
                    (customer.GroupId, cancellationToken);
                var salaryCurrency = salary?.Currency ?? "???";

                logger.LogWarning
                    ("UpdateSaleLineItem: currency mismatch for product {ProductId} ('{ProductName}') priced in {ProductCurrency}; customer {CustomerId} salary currency is {SalaryCurrency}. Rejecting update on sale {SaleId}.",
                    product.Id, product.Name, lineItem.UnitPrice.Currency, sale.CustomerId, salaryCurrency, sale.Id);

                return Result.Failure(
                    CurrencyMismatchErrors.Format(product.Name, lineItem.UnitPrice.Currency, salaryCurrency));
            }
        }

        // ------------------------------------------------------------------
        // SALARY BUDGET CHECK (enforced when LimitMode is SalaryOnly or Both).
        //
        // delta is SIGNED — positive when the qty increases (consuming more
        // budget), negative when it decreases (freeing budget). The check
        // only applies when delta > 0; removing items never violates the
        // salary budget.
        //
        //   delta       = (newQty - oldQty) × lineItem.UnitPrice.Amount
        //   newConsumed = budgetInfo.Consumed + delta
        //   Check:      delta ≤ budgetInfo.Remaining
        //
        // Note: budgetInfo.Consumed already includes the customer's current
        // draft cart total (with the line at its OLD qty). After update,
        // the draft total changes by `delta` — same change to consumed.
        // ------------------------------------------------------------------
        var delta = (command.Quantity - lineItem.Quantity) * lineItem.UnitPrice.Amount;

        if (customer.GroupId is not null
            && delta > 0m
            && await purchaseLimitPolicy.IsSalaryBudgetEnforcedAsync(cancellationToken))
        {
            var budgetInfo = await salaryBudgetService.GetBudgetInfoAsync
                (sale.CustomerId, cancellationToken);

            if (budgetInfo is not null && delta > budgetInfo.Remaining)
            {
                logger.LogWarning
                    ("UpdateSaleLineItem: salary budget exceeded for product {ProductId} on sale {SaleId} (customer {CustomerId}, delta {Delta}, remaining budget {Remaining}, currency {Currency}).",
                    product.Id, sale.Id, sale.CustomerId, delta, budgetInfo.Remaining, budgetInfo.Salary.Currency);

                return Result.Failure(
                    SalaryBudgetExceededErrors.Format
                        (product.Name, delta, budgetInfo.Remaining, budgetInfo.Salary.Currency));
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
        // Delegate to the aggregate. It re-checks the purchase limit and
        // throws DomainException on violation. We catch that here and
        // return a friendly Result.Failure (rather than letting the
        // exception propagate to the Blazor caller, where it would surface
        // as a generic "something went wrong" toast).
        //
        // The UI clamps the qty selector to Min(MyPurchaseLimit,
        // Max(CurrentStock, Quantity)) — so under normal use the limit
        // is never exceeded here. But two edge cases still trigger this:
        //   (a) Multi-tab — user updates the same line from two tabs
        //       simultaneously. The reload between the two calls may
        //       already have pushed the line past the limit.
        //   (b) Admin lowered the limit between the user's cart-load
        //       and their Update click.
        // ------------------------------------------------------------------
        try
        {
            sale.UpdateLineItemQuantity(
                lineItemId: command.LineItemId,
                newQuantity: command.Quantity,
                purchaseLimit: purchaseLimit);
        }
        catch (DomainException ex) when (ex.Message.Contains("Purchase limit", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning
                ("UpdateSaleLineItem: purchase-limit exceeded for product {ProductId} on sale {SaleId} (customer {CustomerId}, limit {Limit}, requested {Qty}). Domain message: {Msg}",
                lineItem.ProductId, sale.Id, sale.CustomerId, purchaseLimit, command.Quantity, ex.Message);

            // Use the culture-neutral error format. The UI layer
            // (Cart.razor + Products.razor mini-cart) intercepts this
            // with PurchaseLimitErrors.TryParse and substitutes a
            // localized message that does NOT mention "groups".
            if (purchaseLimit.HasValue)
            {
                return Result.Failure(
                    PurchaseLimitErrors.Format(lineItem.ProductName, purchaseLimit.Value));
            }

            // Defensive — the catch only fires when purchaseLimit was
            // set, but if it's somehow null, fall back to a generic
            // English message.
            return Result.Failure(
                $"Purchase limit exceeded for '{lineItem.ProductName}'.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "UpdateSaleLineItem: line {LineItemId} on sale {SaleId} set to quantity {Quantity}.",
            command.LineItemId, sale.Id, command.Quantity);

        return Result.Success();
    }
}
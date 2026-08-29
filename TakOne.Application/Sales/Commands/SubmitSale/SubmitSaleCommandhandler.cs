using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.SubmitSale;

public sealed class SubmitSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        SubmitSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        ISalaryBudgetService salaryBudgetService,
        ICartMutationLock cartMutationLock,
        ISaleStateLock saleStateLock,
        ISaleNumberGenerator saleNumberGenerator,
        IUnitOfWork unitOfWork,
        ILogger<SubmitSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        // Load with line items because the aggregate's Submit() requires
        // at least one line item and a positive total — it will inspect
        // the line items collection.
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // ------------------------------------------------------------------
        // ACQUIRE PER-USER CART MUTATION LOCK (Step 4 wiring).
        //
        // Acquired on sale.CustomerId (NOT currentUser.UserId) so that
        // staff-editing-on-behalf serializes on the customer's lock. The
        // submit-time re-checks below (currency, salary budget) read the
        // authoritative state under the lock — a concurrent invocation
        // cannot add a line to the cart between our budget read and our
        // Submit() call.
        //
        // The lock is acquired AFTER the sale load + ownership check (we
        // need sale.CustomerId, and the ownership check must NOT block on
        // the lock — the caller may be unauthorized).
        // ------------------------------------------------------------------
        await using var _cartLockHandle = await cartMutationLock.AcquireAsync
            (sale.CustomerId, cancellationToken);

        // ------------------------------------------------------------------
        // ACQUIRE PER-SALE STATE-TRANSITION LOCK (race-condition fix).
        //
        // The cart lock serializes concurrent cart-mutating invocations on
        // the same customer. But a STAFF member can simultaneously Approve
        // / Cancel / MarkAsInvoiced the SAME sale (which acquires the
        // sale-state lock on sale.Id). Without also acquiring the
        // sale-state lock here, a Submit × Approve race can occur:
        //   - Staff clicks Approve on sale X.
        //   - Customer starts Submit on sale X.
        //   - Both pass their initial guards, both call SaveChangesAsync.
        //   - The loser's SaveChanges throws DbUpdateConcurrencyException.
        // Acquiring the sale-state lock here serializes Submit against
        // any concurrent state-transition on the SAME sale.
        // ------------------------------------------------------------------
        await using var _saleStateLockHandle = await saleStateLock.AcquireAsync
            (sale.Id, cancellationToken);

        // Re-load the sale after acquiring the lock — a concurrent
        // invocation may have added / removed lines or even submitted the
        // sale. The first load was needed to validate ownership BEFORE
        // blocking on the lock; this second load gives us the authoritative
        // post-lock state for the budget re-check.
        sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            logger.LogWarning(
                "SubmitSale: sale {SaleId} disappeared after acquiring cart lock.",
                command.SaleId);
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        if (sale.CreatedByUserId != currentUser.UserId)
        {
            // Re-check ownership after re-load (defensive against a
            // concurrent re-assignment — unlikely but cheap to verify).
            logger.LogWarning(
                "SubmitSale: after re-load, sale {SaleId} creator changed from {OrigCreator} to {NewCreator}.",
                sale.Id, currentUser.UserId, sale.CreatedByUserId);
            return Result.Failure(CartConflictErrors.Format());
        }

        if (sale.Status != Domain.Sales.Enums.SaleStatus.Draft)
        {
            // The sale was already submitted by a concurrent invocation.
            logger.LogInformation(
                "SubmitSale: sale {SaleId} is no longer Draft after re-load (status = {Status}). A concurrent invocation likely already submitted it.",
                sale.Id, sale.Status);
            return Result.Failure(CartConflictErrors.Format());
        }

        // ------------------------------------------------------------------
        // The one forbidden thing: a sale must be submitted by its own
        // creator. Customer creates draft + employee submits = NOT allowed.
        // Employee creates on behalf + employee submits = allowed (same person).
        //
        // (Already checked above — re-stated for clarity after the re-load.)
        // ------------------------------------------------------------------
        // Original ownership + status checks were done above BEFORE acquiring the lock.

        // ------------------------------------------------------------------
        // PURCHASE-LIMIT + CURRENCY + SALARY-BUDGET RE-CHECK AT SUBMIT TIME
        // (defense-in-depth).
        //
        // The Add / Update paths already enforced these at the time the
        // line was last touched. But the limits / budget / currency may
        // have CHANGED by an admin between when the user added the item to
        // their cart and when they clicked Submit. Without these re-checks,
        // the user could successfully submit a sale that violates the
        // current limits.
        //
        // We re-resolve the customer's GroupId from the DB (the auth cookie
        // claim is stale), look up each line's product, and reject if any
        // of the following fails:
        //   1. Currency match — every line's product currency must match
        //      the customer's salary currency.
        //   2. Salary budget — the customer's consumed amount must not
        //      exceed their salary (Remaining >= 0). When salary budget
        //      is enforced.
        //   3. Per-product count limit — every line's qty must not exceed
        //      the product's limit for the customer's group.
        //
        // CATEGORY-DEACTIVATION RE-CHECK (also defense-in-depth):
        //   Even if every line passed Add/Update validation, an admin may
        //   have DEACTIVATED the product's Category / SubCategory /
        //   SubSubCategory between the time the line was added and the
        //   time the user clicked Submit. Such products must NOT be
        //   submittable — but their StockQuantity is preserved.
        //
        // Single round-trip for the customer + single round-trip for all
        // line-item products. The failure errors use the stable-code
        // pattern so the UI can localize them without mentioning "groups".
        // ------------------------------------------------------------------
        var customer = await userRepository.GetByIdAsync(sale.CustomerId, cancellationToken);
        if (customer is null)
        {
            logger.LogError(
                "SubmitSale: customer {CustomerId} on sale {SaleId} not found. [{UnexpectedCode}]",
                sale.CustomerId, sale.Id,
                UnexpectedErrorCodes.SubmitSale_CustomerDisappeared);

            // Wire-format prefix "UE|" — the UI's ErrorDisplayService.Localize
            // recognizes the prefix and substitutes a localized
            // "An unexpected error occurred. Error code: {0}" message.
            return Result.Failure(
                $"UE|{UnexpectedErrorCodes.SubmitSale_CustomerDisappeared}");
        }

        // ------------------------------------------------------------------
        // GROUP-MEMBERSHIP GUARD (Step 12-a runtime fix).
        //
        //    Business rule: a user MUST belong to a CustomerGroup to
        //    submit any sale. Without a group, the customer has no salary
        //    budget / currency / per-product cap to enforce — the sale
        //    would escape all purchase-limit checks at the final submit
        //    step, defeating the entire salary/budget feature.
        //
        //    This applies to ALL customers (staff submitting on behalf of
        //    a customer included — the customer must have a group). It
        //    blocks the exact bug reported in the Step 12-a runtime
        //    review: a no-group user could submit sales without any
        //    limit enforcement.
        //
        //    The error uses NoCustomerGroupErrors.Format so the UI layer
        //    (Cart page) can localize it without exposing the internal
        //    "customer group" concept.
        // ------------------------------------------------------------------
        if (customer.GroupId is null)
        {
            logger.LogWarning
                ("SubmitSale: customer {CustomerId} on sale {SaleId} has no customer group assigned. " +
                 "Rejecting submit.",
                customer.Id, sale.Id);

            return Result.Failure(NoCustomerGroupErrors.Format());
        }

        if (sale.LineItems.Count > 0)
        {
            var lineProductIds = sale.LineItems.Select(li => li.ProductId).Distinct().ToList();
            var lineProducts = await productRepository.GetByIdsAsync(lineProductIds, cancellationToken);
            var lineProductById = lineProducts.ToDictionary(p => p.Id);

            // ---- Pass 1: category-deactivation check (applies to ALL
            //              customers, including staff with no GroupName).
            //              A single product whose category was deactivated
            //              fails the entire submit — the user must remove
            //              the line or ask an admin to reactivate the
            //              category.
            foreach (var line in sale.LineItems)
            {
                if (!lineProductById.TryGetValue(line.ProductId, out var lineProduct))
                {
                    // Defensive — the product existed when the line was
                    // added. If it's gone now, the database is in an
                    // inconsistent state. Log + fail submit.
                    logger.LogError(
                        "SubmitSale: product {ProductId} on sale {SaleId} line {LineId} no longer exists.",
                        line.ProductId, sale.Id, line.Id);

                    return Result.Failure(
                        $"Product '{line.ProductName}' no longer exists. Remove the line before submitting.");
                }

                var hierarchyActive = await categoryRepository.IsProductCategoryHierarchyActiveAsync
                    (
                    lineProduct.CategoryId,
                    lineProduct.SubCategoryId,
                    lineProduct.SubSubCategoryId,
                    cancellationToken
                    );

                if (!hierarchyActive)
                {
                    logger.LogWarning(
                        "SubmitSale: product {ProductId} ('{ProductName}') on sale {SaleId} line {LineId} is under a deactivated category " +
                        "(Category={CategoryId}, Sub={SubCategoryId}, SubSub={SubSubCategoryId}). Rejecting submit.",
                        lineProduct.Id, lineProduct.Name, sale.Id, line.Id,
                        lineProduct.CategoryId, lineProduct.SubCategoryId, lineProduct.SubSubCategoryId);

                    return Result.Failure(
                        CategoryDeactivatedErrors.Format(lineProduct.Name));
                }
            }

            // ---- Pass 2: purchase-limit check (ONLY for customers with
            //              a GroupId — staff have no per-product cap).
            //              Uses the policy (replaces inline GetPurchaseLimitForGroup)
            //              so the LimitMode is respected: when mode is SalaryOnly,
            //              GetCountLimitsAsync returns no limits and the check is skipped.
            //
            //              Round 2 N+1 fix: this used to loop the single-product
            //              GetCountLimitAsync PER LINE — each call re-loaded a
            //              Product this handler had ALREADY batch-loaded into
            //              lineProductById. The batched variant resolves every
            //              line's limit in ONE query with identical semantics
            //              (missing key / null value both mean "no limit").
            if (customer.GroupId is not null)
            {
                var limitByProductId = await purchaseLimitPolicy.GetCountLimitsAsync
                    (lineProductIds, customer.GroupId, cancellationToken);

                foreach (var line in sale.LineItems)
                {
                    // lineProductById was populated above; the TryGetValue
                    // already succeeded in Pass 1, so it will succeed here
                    // too. Defensive null-check kept for safety.
                    if (!lineProductById.TryGetValue(line.ProductId, out var lineProduct))
                    {
                        continue;
                    }

                    var limitVo = limitByProductId.TryGetValue(lineProduct.Id, out var batchedLimit)
                        ? batchedLimit
                        : null;
                    if (limitVo is not null && line.Quantity > limitVo.Value)
                    {
                        logger.LogWarning(
                            "SubmitSale: purchase-limit exceeded at submit time for product {ProductId} on sale {SaleId} " +
                            "(customer {CustomerId}, limit {Limit}, line qty {Qty}).",
                            line.ProductId, sale.Id, sale.CustomerId, limitVo.Value, line.Quantity);

                        return Result.Failure(
                            PurchaseLimitErrors.Format(line.ProductName, limitVo.Value));
                    }
                }

                // ---- Pass 2b: currency match re-check (always enforced).
                //
                // For each line, the product's current price currency must
                // match the customer's salary currency. The price snapshot on
                // the line is from when the line was added; we use that for
                // the error message, but the policy re-reads the product to
                // resolve its currency (so a currency change via admin updating
                // the product's price is caught here).
                //
                // Note: changing a customer's salary CURRENCY is not allowed
                // via UpdateCustomerGroupSalary (preserves currency — admin
                // must deactivate + create new). So this check is purely
                // defensive against the product's price having been edited.
                //
                // Round 2 N+1 fix: the mismatch set is resolved ONCE for the
                // whole sale (single group load + single product batch load)
                // instead of re-loading the same group and product per line.
                // ------------------------------------------------------------------
                var mismatchedProductIds = await purchaseLimitPolicy.GetCurrencyMismatchedProductIdsAsync
                    (lineProductIds, customer.GroupId, cancellationToken);

                foreach (var line in sale.LineItems)
                {
                    if (!lineProductById.TryGetValue(line.ProductId, out var lineProduct))
                    {
                        continue;
                    }

                    if (mismatchedProductIds.Contains(lineProduct.Id))
                    {
                        var salary = await salaryBudgetService.GetGroupSalaryAsync
                            (customer.GroupId, cancellationToken);
                        var salaryCurrency = salary?.Currency ?? "???";

                        logger.LogWarning(
                            "SubmitSale: currency mismatch at submit time for product {ProductId} on sale {SaleId} " +
                            "(line currency {LineCurrency}, customer salary currency {SalaryCurrency}).",
                            lineProduct.Id, sale.Id, line.UnitPrice.Currency, salaryCurrency);

                        return Result.Failure(
                            CurrencyMismatchErrors.Format(lineProduct.Name, line.UnitPrice.Currency, salaryCurrency));
                    }
                }

                // ---- Pass 2c: salary-budget re-check (defense-in-depth).
                //
                // Submitting the sale doesn't change the consumed amount
                // (the draft cart total just becomes the submitted sale total —
                // same query result). But the salary may have been LOWERED
                // mid-month after the user added items to their cart, in
                // which case the consumed amount now exceeds the salary.
                //
                // We reject submit if Remaining < 0 — meaning consumed > salary.
                // The customer has to wait until next month's budget resets
                // (or ask an admin to raise their salary / remove some items
                // from the cart).
                //
                // Only runs when salary budget is enforced (SalaryOnly / Both).
                // ------------------------------------------------------------------
                if (await purchaseLimitPolicy.IsSalaryBudgetEnforcedAsync(cancellationToken))
                {
                    var budgetInfo = await salaryBudgetService.GetBudgetInfoAsync
                        (sale.CustomerId, cancellationToken);

                    if (budgetInfo is not null && budgetInfo.Remaining < 0m)
                    {
                        // sale.Total is the cart being submitted. It's part of
                        // budgetInfo.Consumed (cart-reserves-budget rule). The
                        // error reports sale.Total as the lineTotal and
                        // budgetInfo.Remaining as the (negative) remainingBudget.
                        logger.LogWarning(
                            "SubmitSale: salary budget exceeded at submit time for sale {SaleId} " +
                            "(customer {CustomerId}, saleTotal {SaleTotal}, consumed {Consumed}, salary {Salary}, remaining {Remaining}, currency {Currency}). " +
                            "The salary was likely lowered after the cart was assembled.",
                            sale.Id, sale.CustomerId, sale.Total.Amount, budgetInfo.Consumed,
                            budgetInfo.Salary.Amount, budgetInfo.Remaining, budgetInfo.Salary.Currency);

                        return Result.Failure(
                            SalaryBudgetExceededErrors.Format(
                                // Empty productName marks this as a WHOLE-CART
                                // budget failure at submit time (not a single
                                // line). The Cart page detects the empty name
                                // and shows the "SalaryBudgetExceededCart"
                                // message; passing the SaleNumber here would
                                // render "Adding 'INT-۱۴۰۵-۰۰…' would exceed
                                // your budget" — confusing, since the sale
                                // number is not a product the user "added".
                                /* productName */ string.Empty,
                                /* lineTotal  */ sale.Total.Amount,
                                /* remaining  */ budgetInfo.Remaining,
                                /* currency   */ budgetInfo.Salary.Currency));
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // EMPTY-CART GUARD (defense-in-depth).
        //
        // The Sale aggregate's Submit() method throws DomainException if
        // the sale has no line items — which is caught by middleware and
        // converted to Result.Failure with an English message
        // ("Cannot submit a sale with no line items."). That English
        // message leaks to the UI even in Persian mode.
        //
        // More importantly, this guard is the LAST line of defense against
        // the "empty order" bug: if a ghost draft (an empty Draft row from
        // a buggy code path — see QuickReorderLastSaleCommandHandler's
        // commentary on the ghost-draft bug) ever makes it to the Submit
        // flow, this check blocks it from becoming a Pending order. The
        // user's cart page ALSO guards against this (the Submit button is
        // disabled when the cart is empty, and HasStockIssue / HasLimitIssue
        // re-check in the handler), but defense-in-depth means we don't
        // trust any single layer.
        //
        // Returns a stable, culture-neutral error code that the UI
        // localizes — mirroring the PurchaseLimitErrors / StockErrors /
        // CategoryDeactivatedErrors pattern.
        //
        // NOTE: This guard runs BEFORE sale-number allocation. An empty
        // cart must NOT burn a permanent sequence number — the allocation
        // below costs a row update in SaleSequenceCounters and (under the
        // gap-burning semantics of the generator) cannot be rolled back.
        // Blocking empty carts here keeps the permanent sequence clean.
        // ------------------------------------------------------------------
        if (sale.LineItems.Count == 0)
        {
            logger.LogWarning
                ("SubmitSale: sale {SaleId} has 0 line items. " +
                 "Blocking submit — an empty draft should never reach the submit flow " +
                 "(the QuickReorder handler's compute-first architecture prevents this, " +
                 "and the Cart page disables the Submit button for empty carts). " +
                 "No sale number was allocated.",
                sale.Id);

            return Result.Failure("SubmitEmptyCart");
        }

        // ------------------------------------------------------------------
        // SALE NUMBER ALLOCATION (B2 deferred-allocation design).
        //
        // The permanent SaleNumber is allocated HERE — at submit time, NOT
        // at draft creation. Drafts are disposable carts; many are created
        // and abandoned. Allocating at draft creation would burn permanent
        // sequence numbers on drafts that never get submitted, producing
        // gaps in the audit trail of POSTED sales.
        //
        // By allocating here, the permanent sequence stays clean: gaps only
        // come from voided/cancelled submitted sales, which is the correct,
        // auditable ERP behavior (matching SAP, Oracle, NetSuite).
        //
        // The generator (SaleNumberGenerator) uses an atomic UPDATE...OUTPUT
        // on the SaleSequenceCounters table — the allocation commits
        // independently of Wolverine's ambient transaction, so the number
        // is "burned" even if SaveChangesAsync later fails. This is
        // intentional: it prevents two concurrent submits from getting the
        // same number, and the gap (if SaveChanges fails) is acceptable
        // and auditable.
        //
        // We allocate AFTER all validation passes (empty-cart guard,
        // purchase-limit re-check, category-deactivation re-check) so that
        // a rejected submit does NOT burn a number.
        // ------------------------------------------------------------------
        var saleNumber = await saleNumberGenerator.NextAsync(cancellationToken);

        // Delegate to the aggregate. Submit() enforces:
        //   - sale is in Draft status (throws otherwise)
        //   - sale has at least one line item (throws otherwise — redundant
        //     with the guard above, but the domain doesn't trust the
        //     application layer either)
        //   - sale total is positive (throws otherwise)
        //   - saleNumber is non-null (throws otherwise — programmer error)
        // Submit() assigns the SaleNumber to the Sale aggregate (it was null
        // while the sale was a Draft).
        // DomainException is caught by middleware and converted to Result.Failure.
        sale.Submit(saleNumber);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "SubmitSale: sale {SaleId} ({SaleNumber}) submitted by user {UserId}.",
            sale.Id, sale.SaleNumber, currentUser.UserId);

        return Result.Success();
    }
}
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Sales.Commands.QuickReorderLastSale;

/// <summary>
/// Handler for <see cref="QuickReorderLastSaleCommand"/>. See the command
/// file for the full business rules + quantity-clamping rationale.
/// </summary>
public sealed class QuickReorderLastSaleCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        QuickReorderLastSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUserRepository userRepository,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        ISalaryBudgetService salaryBudgetService,
        ICartMutationLock cartMutationLock,
        IUnitOfWork unitOfWork,
        ILogger<QuickReorderLastSaleCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // ACQUIRE PER-USER CART MUTATION LOCK (Step 4 wiring).
        //
        // Quick Reorder is a SELF-BUY flow (the current user is both the
        // customer and the creator — see Sale.Create below). The lock is
        // therefore acquired on currentUser.UserId.
        //
        // The lock serializes ALL cart mutations for this user. Without
        // it, a concurrent Add-to-cart or Remove-line could change the
        // draft state between our read of `existingDraft` (line 4 below)
        // and our add-line loop — producing inconsistent budget / limit
        // clamping.
        //
        // We acquire the lock BEFORE reading the user / last sale so that
        // the salary budget check below uses the authoritative post-lock
        // value (no concurrent invocation can mutate the cart between
        // our budget read and our add-line commit).
        // ------------------------------------------------------------------
        await using var _cartLockHandle = await cartMutationLock.AcquireAsync
            (currentUser.UserId, cancellationToken);

        // ------------------------------------------------------------------
        // 1. Find the user's most-recent submitted sale (with line items).
        //    Null means the user has never submitted an order → friendly
        //    failure, not a 500.
        // ------------------------------------------------------------------
        var lastSale = await saleRepository.GetLastSubmittedSaleForUserAsync
            (currentUser.UserId, cancellationToken);

        if (lastSale is null)
        {
            logger.LogInformation
                ("QuickReorderLastSale: user {UserId} has no submitted orders to repeat.",
                currentUser.UserId);

            return Result<Guid>.Failure("شما سفارش قبلی برای تکرار ندارید.");
        }

        // ------------------------------------------------------------------
        // 2. Resolve the current user's per-product purchase limits in one
        //    round-trip. We need to fetch the products anyway (for stock +
        //    current price + name snapshot), and the limits come with the
        //    product aggregate. We use IProductRepository.GetByIdsReadOnlyAsync
        //    — the read-only variant that does NOT track entities in EF Core's
        //    change tracker. This handler only READS the products (price/name/
        //    stock snapshot); the Sale aggregate is the only entity we WRITE.
        //    Brutal Code Review v3 finding #18: the previous call used the
        //    TRACKED GetByIdsAsync, which caused EF Core's change tracker to
        //    confuse owned Money instances between the tracked Product and the
        //    new SaleLineItem, throwing DbUpdateConcurrencyException on save.
        // ------------------------------------------------------------------
        var productIds = lastSale.LineItems.Select(li => li.ProductId).Distinct().ToList();
        var products = await productRepository.GetByIdsReadOnlyAsync(productIds, cancellationToken);
        var productById = products.ToDictionary(p => p.Id);

        // ------------------------------------------------------------------
        // 3. COMPUTE THE LINES TO ADD — BEFORE creating or fetching any
        //    draft. This is the key architectural change from the previous
        //    implementation. Previously, the handler created/fetched the
        //    draft FIRST (calling saleRepository.AddAsync, which stages the
        //    Sale in EF Core's change tracker), THEN iterated the last
        //    sale's lines and clamped each to min(originalQty, currentStock,
        //    remainingLimit). If all lines clamped to 0 (stock exhausted /
        //    limit exhausted), the handler returned without calling
        //    SaveChanges — relying on the change tracker being discarded at
        //    DbContext disposal.
        //
        //    That approach had a latent risk: if ANY code path between
        //    AddAsync and the handler's return called SaveChanges (e.g. a
        //    Wolverine middleware, an audit interceptor, a domain-event
        //    handler that persists something), the staged-but-empty draft
        //    would be persisted as a ghost — an empty Sale row in Draft
        //    status that blocks all future Add-to-cart attempts (the user
        //    would see "you already have an active cart" but the cart
        //    appears empty). This is the "empty order" bug the user
        //    reported: stock went to 0, user hit Quick Reorder, an empty
        //    Draft was created and somehow persisted.
        //
        //    The new approach eliminates the risk entirely: we compute the
        //    list of (product, clampedQty) pairs into a local list FIRST.
        //    Only if the list is non-empty do we create/fetch the draft and
        //    add the lines. If the list is empty, NO draft is ever staged
        //    in the change tracker — there is nothing to accidentally
        //    persist.
        //
        //    If the list is empty AND the user already has an existing
        //    ghost draft from the old buggy code (self-healing), we
        //    hard-delete it.
        // ------------------------------------------------------------------
        // 2b. Resolve the current caller's GroupId FRESH from the DB
        //     (after lock acquired — see lock block above).
        //     The GroupId claim on the auth cookie is a snapshot from
        //     login time and goes stale when an admin reassigns the user's
        //     group after they're already logged in. Reading from the DB
        //     guarantees the limit reflects the user's CURRENT group, so
        //     the per-product clamping below is correct without requiring
        //     a re-login.
        //
        //     Resolve the salary budget info ONCE here (when budget is
        //     enforced) so we can clamp the per-line qty against the
        //     remaining budget across the entire reorder batch. The
        //     remaining budget DECREMENTS as we add lines — if the user
        //     is repeating 5 lines that together exceed the budget, we
        //     clamp each line proportionally (oldest first) until the
        //     remaining budget is 0, then skip the rest.
        // ------------------------------------------------------------------
        var freshUser = await userRepository.GetByIdAsync(currentUser.UserId, cancellationToken);
        var groupId = freshUser?.GroupId;

        // ------------------------------------------------------------------
        // 2b-1. GROUP-MEMBERSHIP GUARD (Step 12-a runtime fix).
        //
        //       Business rule: a user MUST belong to a CustomerGroup to
        //       make any purchase. Reject upfront with a culture-neutral
        //       error so the UI can localize it without exposing the
        //       "customer group" concept to the end user.
        //
        //       See NoCustomerGroupErrors.cs for the rationale and the
        //       matching UI-side TryParse hook.
        // ------------------------------------------------------------------
        if (groupId is null)
        {
            logger.LogWarning
                ("QuickReorderLastSale: user {UserId} has no customer group assigned. " +
                 "Rejecting quick-reorder of last sale {LastSaleId}.",
                currentUser.UserId, lastSale.Id);

            return Result<Guid>.Failure(NoCustomerGroupErrors.Format());
        }

        var salaryBudgetEnforced = groupId is not null
            && await purchaseLimitPolicy.IsSalaryBudgetEnforcedAsync(cancellationToken);
        var salaryBudgetInfo = salaryBudgetEnforced
            ? await salaryBudgetService.GetBudgetInfoAsync(currentUser.UserId, cancellationToken)
            : null;
        var remainingBudget = salaryBudgetInfo?.Remaining ?? decimal.MaxValue;

        var linesToAdd = new List<(Product Product, int Quantity, int? PurchaseLimit)>();

        // We need the existing draft's line quantities to compute "remaining
        // limit" — but only if a draft exists. Fetch it now (we still need
        // it later if we have lines to add).
        var existingDraft = await saleRepository.GetActiveDraftForUserAsync
            (currentUser.UserId, cancellationToken);

        foreach (var line in lastSale.LineItems)
        {
            // Look up the current product. If it was hard-deleted (shouldn't
            // happen — products are soft-deactivated, not removed), skip.
            if (!productById.TryGetValue(line.ProductId, out var product))
            {
                logger.LogWarning
                    ("QuickReorderLastSale: product {ProductId} on last sale {SaleId} " +
                     "no longer exists; skipping line.",
                    line.ProductId, lastSale.Id);
                continue;
            }

            // ------------------------------------------------------------------
            // CURRENCY MATCH CHECK (always enforced, regardless of LimitMode).
            // A customer whose salary is in IRR cannot buy a product priced
            // in USD. Skip the line silently (the user can manually add it
            // if they switch groups — but that's an admin action).
            // ------------------------------------------------------------------
            if (groupId is not null)
            {
                var currencyOk = await purchaseLimitPolicy.IsCurrencyMatchAsync
                    (product.Id, groupId, cancellationToken);

                if (!currencyOk)
                {
                    logger.LogInformation
                        ("QuickReorderLastSale: skipping line for product {ProductId} " +
                         "(currency mismatch with user {UserId}'s salary).",
                        product.Id, currentUser.UserId);
                    continue;
                }
            }

            // Current per-group limit for this caller (null for staff).
            int? purchaseLimit = null;
            if (groupId is not null)
            {
                purchaseLimit = await purchaseLimitPolicy.GetCountLimitAsync
                    (product.Id, groupId, cancellationToken);
            }

            // Existing draft quantity for this product (0 if no draft or
            // product not yet in draft).
            var existingInDraft = existingDraft?.LineItems
                .Where(li => li.ProductId == line.ProductId)
                .Sum(li => li.Quantity) ?? 0;

            // Clamp: min(original, stock, remainingLimit, remainingBudget).
            var clamped = line.Quantity;
            if (clamped > product.StockQuantity)
            {
                clamped = product.StockQuantity;
            }
            if (purchaseLimit.HasValue)
            {
                var remaining = purchaseLimit.Value - existingInDraft;
                if (clamped > remaining)
                {
                    clamped = remaining;
                }
            }

            // ------------------------------------------------------------------
            // SALARY BUDGET CLAMP: each line must not push the consumed
            // amount past the salary. We clamp the qty DOWN so the line's
            // total fits within the remaining budget. If the line's unit
            // price alone exceeds the remaining budget, we skip the line.
            //
            // remainingBudget is updated AFTER clamping each line so the
            // NEXT line in the loop sees the decremented value.
            // ------------------------------------------------------------------
            if (salaryBudgetEnforced && salaryBudgetInfo is not null)
            {
                var unitPrice = product.Price.Amount;
                if (unitPrice <= 0m || unitPrice > remainingBudget)
                {
                    logger.LogInformation
                        ("QuickReorderLastSale: skipping line for product {ProductId} " +
                         "(unit price {UnitPrice} exceeds remaining budget {Remaining}, currency {Currency}).",
                         product.Id, unitPrice, remainingBudget, salaryBudgetInfo.Salary.Currency);
                    continue;
                }

                // maxAffordableQty = floor(remainingBudget / unitPrice)
                var maxAffordableQty = (int)Math.Floor(remainingBudget / unitPrice);
                if (clamped > maxAffordableQty)
                {
                    clamped = maxAffordableQty;
                }
            }

            if (clamped < 1)
            {
                // Limit already exhausted in draft OR stock is 0 OR budget
                // exhausted. Skip silently.
                continue;
            }

            // Decrement the remaining budget by the line's total cost.
            if (salaryBudgetEnforced && salaryBudgetInfo is not null)
            {
                remainingBudget -= clamped * product.Price.Amount;
            }

            linesToAdd.Add((product, clamped, purchaseLimit));
        }

        // ------------------------------------------------------------------
        // 4. If nothing can be added, handle the empty case — WITHOUT ever
        //    staging a new draft in the change tracker.
        // ------------------------------------------------------------------
        if (linesToAdd.Count == 0)
        {
            // Self-healing: if the user already has a ghost draft from the
            // old buggy code (an empty Draft with 0 line items), hard-delete
            // it so the user's cart state is clean.
            if (existingDraft is not null && existingDraft.LineItems.Count == 0)
            {
                await saleRepository.DeleteAsync(existingDraft, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);

                logger.LogInformation
                    ("QuickReorderLastSale: user {UserId} repeated sale {LastSaleId} " +
                     "but added 0 lines. Existing ghost draft {DraftId} was hard-deleted " +
                     "to restore a clean cart state.",
                    currentUser.UserId, lastSale.Id, existingDraft.Id);
            }
            else
            {
                logger.LogInformation
                    ("QuickReorderLastSale: user {UserId} repeated sale {LastSaleId} " +
                     "but added 0 lines (all skipped due to stock/limit). " +
                     "No draft was created or modified.",
                    currentUser.UserId, lastSale.Id);
            }

            // Check if the user had an existing draft with line items that
            // we couldn't add anything NEW to (stock/limit exhausted for all
            // last-sale lines). That's a different message than "nothing to
            // add at all".
            if (existingDraft is not null && existingDraft.LineItems.Count > 0)
            {
                return Result<Guid>.Failure("QuickReorderNothingNewAdded");
            }

            return Result<Guid>.Failure("QuickReorderNothingToAdd");
        }

        // ------------------------------------------------------------------
        // 5. We have at least one line to add. Now create or reuse the
        //    draft, add the lines, and SaveChanges.
        // ------------------------------------------------------------------
        Sale draft;

        if (existingDraft is not null)
        {
            // Reuse the existing draft — add to its existing lines.
            draft = existingDraft;
        }
        else
        {
            // No active draft → create a fresh one (B2 deferred-allocation
            // design: no SaleNumber is allocated at draft creation; the
            // permanent number is assigned only at submit time). The
            // current user is both customer and creator (self-buy flow).
            draft = Sale.Create
                (
                customerId: currentUser.UserId,
                customerName: currentUser.FullName,
                createdByUserId: currentUser.UserId,
                createdByName: currentUser.FullName
                );

            await saleRepository.AddAsync(draft, cancellationToken);
        }

        // Add each computed line. The aggregate re-checks the limit + stock
        // invariants (defense-in-depth); our clamping above ensures those
        // checks pass.
        foreach (var (product, quantity, purchaseLimit) in linesToAdd)
        {
            draft.AddLineItem
                (
                productId: product.Id,
                productName: product.Name,
                quantity: quantity,
                // SNAPSHOT the price — pass a NEW Money instance, NOT
                // product.Price by reference. Every other caller correctly
                // snapshots; this one was the sole by-reference offender.
                // EF Core's change tracker confuses owned Money instances
                // shared between the Product and the SaleLineItem and
                // throws DbUpdateConcurrencyException on save. Brutal
                // Code Review v3 finding #18.
                unitPrice: new Money(product.Price.Amount, product.Price.Currency),
                purchaseLimit: purchaseLimit
                );
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("QuickReorderLastSale: user {UserId} repeated sale {LastSaleId} into draft {DraftId}. " +
             "Added {Added} lines, skipped {Skipped} (out of stock or limit-exhausted).",
            currentUser.UserId, lastSale.Id, draft.Id, linesToAdd.Count,
            lastSale.LineItems.Count - linesToAdd.Count);

        return Result<Guid>.Success(draft.Id);
    }
}
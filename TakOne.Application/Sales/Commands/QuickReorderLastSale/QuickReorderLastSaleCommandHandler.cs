using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;

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
        ISaleNumberGenerator saleNumberGenerator,
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
        // 2. Find or create the user's active Draft. We need it loaded WITH
        //    line items so we can compute "remaining limit" = limit - existing
        //    quantity already in the draft for the same product.
        //
        //    GHOST-DRAFT GUARD:
        //      We track whether WE created the draft in this handler invocation
        //      (`createdNewDraftInThisCall`). If we did AND the reorder adds
        //      zero lines (everything skipped due to stock/limit), we will NOT
        //      persist the empty draft — see step 5. This prevents the
        //      "ghost draft" bug where an empty Sales row in Draft status
        //      blocked all future Add-to-cart attempts.
        // ------------------------------------------------------------------
        var createdNewDraftInThisCall = false;

        var draft = await saleRepository.GetActiveDraftForUserAsync
            (currentUser.UserId, cancellationToken);

        if (draft is null)
        {
            // No active draft → create a fresh one. The current user is both
            // customer and creator (self-buy flow).
            var saleNumber = await saleNumberGenerator.NextAsync(cancellationToken);

            draft = Sale.Create
                (
                customerId: currentUser.UserId,
                customerName: currentUser.FullName,
                saleNumber: saleNumber,
                createdByUserId: currentUser.UserId,
                createdByName: currentUser.FullName
                );

            await saleRepository.AddAsync(draft, cancellationToken);
            createdNewDraftInThisCall = true;
        }

        // ------------------------------------------------------------------
        // 3. Resolve the current user's per-product purchase limits in one
        //    round-trip. We need to fetch the products anyway (for stock +
        //    current price + name snapshot), and the limits come with the
        //    product aggregate. We use IProductRepository.GetByIdsAsync.
        // ------------------------------------------------------------------
        var productIds = lastSale.LineItems.Select(li => li.ProductId).Distinct().ToList();
        var products = await productRepository.GetByIdsAsync(productIds, cancellationToken);
        var productById = products.ToDictionary(p => p.Id);

        // ------------------------------------------------------------------
        // 4. Iterate the last sale's lines and re-add each to the draft,
        //    clamping quantity to: min(originalQty, currentStock,
        //    remainingLimit). Lines that clamp to 0 are skipped silently.
        // ------------------------------------------------------------------
        var groupName = currentUser.GroupName;
        var addedCount = 0;
        var skippedCount = 0;

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

                skippedCount++;
                continue;
            }

            // Current per-group limit for this caller (null for staff).
            int? purchaseLimit = null;
            if (!string.IsNullOrWhiteSpace(groupName))
            {
                purchaseLimit = product.GetPurchaseLimitForGroup(groupName)?.Limit;
            }

            // Existing draft quantity for this product (0 if not yet in draft).
            var existingInDraft = draft.LineItems
                .Where(li => li.ProductId == line.ProductId)
                .Sum(li => li.Quantity);

            // Clamp: min(original, stock, remainingLimit).
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

            if (clamped < 1)
            {
                // Limit already exhausted in draft OR stock is 0. Skip silently.
                skippedCount++;
                continue;
            }

            // Add the line. The aggregate re-checks the limit + stock invariants
            // (defense-in-depth); our clamping above ensures those checks pass.
            draft.AddLineItem
                (
                productId: product.Id,
                productName: product.Name,
                quantity: clamped,
                unitPrice: product.Price,
                purchaseLimit: purchaseLimit
                );

            addedCount++;
        }

        // ------------------------------------------------------------------
        // 5. Persist — with GHOST-DRAFT GUARD.
        //
        //    THREE cases:
        //      A. We created a fresh draft in this call AND added ≥1 line:
        //         SaveChanges inserts the Sale + its new line items. Normal path.
        //
        //      B. We created a fresh draft in this call AND added 0 lines
        //         (everything skipped due to stock/limit): do NOT call
        //         SaveChanges. The draft we AddAsync'd is only staged in the
        //         ChangeTracker — skipping SaveChanges means it is NEVER
        //         written to the DB. The DbContextScope will be disposed at
        //         the end of the Wolverine handler invocation, dropping the
        //         uncommitted tracked entity. This is the fix for the
        //         "ghost draft" bug: previously we always persisted, leaving
        //         an empty Draft row that blocked all future Add-to-cart.
        //
        //      C. We found an EXISTING draft (created in a prior call):
        //         - If that existing draft had ≥1 line item before this call,
        //           the line items we may have added (or not) get saved
        //           normally — no ghost risk.
        //         - If that existing draft was ALREADY empty (a ghost from
        //           the old buggy code) AND we added 0 lines now, hard-delete
        //           it to clean up the ghost. This is self-healing: even
        //           users who already have a ghost draft from before this
        //           fix will have it cleaned up the first time they hit
        //           Quick Reorder.
        // ------------------------------------------------------------------
        if (addedCount == 0)
        {
            if (createdNewDraftInThisCall)
            {
                // Case B — discard the never-persisted draft we staged.
                // Detach would also work; just not calling SaveChanges is
                // enough because AddAsync only stages the entity in the
                // change tracker.
                logger.LogInformation
                    ("QuickReorderLastSale: user {UserId} repeated sale {LastSaleId} " +
                     "but added 0 lines (all skipped due to stock/limit). " +
                     "Newly-created draft was discarded — no empty draft persisted.",
                    currentUser.UserId, lastSale.Id);

                return Result<Guid>.Failure
                    ("QuickReorderNothingToAdd");
            }

            // Case C — existing draft. If it's empty, hard-delete the ghost.
            if (draft.LineItems.Count == 0)
            {
                await saleRepository.DeleteAsync(draft, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);

                logger.LogInformation
                    ("QuickReorderLastSale: user {UserId} repeated sale {LastSaleId} " +
                     "but added 0 lines. Existing ghost draft {DraftId} was hard-deleted " +
                     "to restore a clean cart state.",
                    currentUser.UserId, lastSale.Id, draft.Id);

                return Result<Guid>.Failure
                    ("QuickReorderNothingToAdd");
            }

            // Existing draft with line items, nothing new to add —
            // still SaveChanges so any incidental tracked changes (none
            // in current code, but defensively) commit. No-op for the
            // user's cart state.
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogInformation
                ("QuickReorderLastSale: user {UserId} repeated sale {LastSaleId} into draft {DraftId}. " +
                 "Added {Added} lines, skipped {Skipped} (out of stock or limit-exhausted).",
                currentUser.UserId, lastSale.Id, draft.Id, addedCount, skippedCount);

            return Result<Guid>.Failure
                ("QuickReorderNothingNewAdded");
        }

        // Case A (or Case C with additions) — normal persist.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("QuickReorderLastSale: user {UserId} repeated sale {LastSaleId} into draft {DraftId}. " +
             "Added {Added} lines, skipped {Skipped} (out of stock or limit-exhausted).",
            currentUser.UserId, lastSale.Id, draft.Id, addedCount, skippedCount);

        return Result<Guid>.Success(draft.Id);
    }
}
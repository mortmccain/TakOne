using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Products.Entities;
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
        // 2. Resolve the current user's per-product purchase limits in one
        //    round-trip. We need to fetch the products anyway (for stock +
        //    current price + name snapshot), and the limits come with the
        //    product aggregate. We use IProductRepository.GetByIdsAsync.
        // ------------------------------------------------------------------
        var productIds = lastSale.LineItems.Select(li => li.ProductId).Distinct().ToList();
        var products = await productRepository.GetByIdsAsync(productIds, cancellationToken);
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
        var groupName = currentUser.GroupName;
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

            // Current per-group limit for this caller (null for staff).
            int? purchaseLimit = null;
            if (!string.IsNullOrWhiteSpace(groupName))
            {
                purchaseLimit = product.GetPurchaseLimitForGroup(groupName)?.Limit;
            }

            // Existing draft quantity for this product (0 if no draft or
            // product not yet in draft).
            var existingInDraft = existingDraft?.LineItems
                .Where(li => li.ProductId == line.ProductId)
                .Sum(li => li.Quantity) ?? 0;

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
                continue;
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
                saleNumber: null,
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
                unitPrice: product.Price,
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
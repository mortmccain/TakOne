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
        // ------------------------------------------------------------------
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
        // 5. Persist. Even if addedCount == 0, we may have created a fresh
        //    empty draft (step 2) — SaveChanges will insert it. That's fine:
        //    an empty cart is the same UI state as "no cart", and the user
        //    can add items manually. The alternative (rolling back the empty
        //    draft creation) adds complexity for no UX benefit.
        // ------------------------------------------------------------------
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("QuickReorderLastSale: user {UserId} repeated sale {LastSaleId} into draft {DraftId}. " +
             "Added {Added} lines, skipped {Skipped} (out of stock or limit-exhausted).",
            currentUser.UserId, lastSale.Id, draft.Id, addedCount, skippedCount);

        return Result<Guid>.Success(draft.Id);
    }
}
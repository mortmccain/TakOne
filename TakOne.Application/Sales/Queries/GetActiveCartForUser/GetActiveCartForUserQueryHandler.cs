using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.Queries.GetActiveCartForUser;

/// <summary>
/// Handler for <see cref="GetActiveCartForUserQuery"/>. See the query file
/// for the authorization model + return semantics — this class only
/// implements the load + enrich + project flow.
/// </summary>
public sealed class GetActiveCartForUserQueryHandler
{
    public static async Task<Result<CartDto?>> HandleAsync
        (
        GetActiveCartForUserQuery query,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUserRepository userRepository,
        ISalaryBudgetService salaryBudgetService,
        ILogger<GetActiveCartForUserQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check. Wolverine's AuthorizationMiddleware
        //    has already rejected anonymous calls, but this method may also
        //    be invoked from tests or future hosts that bypass middleware.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetActiveCartForUser: unauthenticated call rejected.");

            return Result<CartDto?>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 0b. Compute the caller's salary budget info ONCE up-front. This
        //     is needed in BOTH branches (sale-is-null + sale-is-loaded) so
        //     the CartBudgetBar can render even when the customer has no
        //     active draft — i.e. "you have X remaining this month, go
        //     browse the catalog" is the right UX for the empty-cart
        //     state. The Step 7 worklog calls this out explicitly: "When
        //     the cart is empty, the bar will show just the salary + monthly
        //     consumed (no cart contribution)".
        //
        //     GetBudgetInfoAsync is cheap + short-circuits internally when:
        //       - The user has no GroupId (staff)         → returns null
        //       - LimitMode == CountOnly                  → returns null
        //       - The user has no group / group not found  → returns null
        //     All three "returns null" outcomes correctly suppress the
        //     CartBudgetBar UI (the page checks _cart?.BudgetInfo is not null
        //     before rendering). No special-casing needed at this layer.
        //
        //     This call is OUTSIDE the cart mutation lock — it's a pure
        //     read. The "cart reserves budget" rule means the consumed
        //     amount INCLUDES the current draft total, so even when a
        //     concurrent mutation is in flight, the snapshot returned
        //     here is internally consistent (the draft total at the
        //     moment of the call is either pre-mutation or post-mutation,
        //     both are valid views).
        // ------------------------------------------------------------------
        var budgetInfo = await salaryBudgetService.GetBudgetInfoAsync(
            currentUser.UserId, cancellationToken);

        // ------------------------------------------------------------------
        // 1. Load the user's active draft (with line items eagerly loaded).
        //    Returns null if the user has no active draft — that's the
        //    "empty cart" state, which is a SUCCESS(null) per the query's
        //    documented return semantics.
        // ------------------------------------------------------------------
        var sale = await saleRepository.GetActiveDraftForUserAsync(
            currentUser.UserId,
            cancellationToken);

        if (sale is null)
        {
            logger.LogInformation
                ("GetActiveCartForUser: user {UserId} has no active draft (empty cart).",
                currentUser.UserId);

            // Empty-cart state STILL returns a CartDto with BudgetInfo populated
            // so the CartBudgetBar renders above the empty-cart panel. The
            // SaleId is Guid.Empty (default) — this is safe because the
            // empty-cart UI branch never dispatches any cart-mutation command
            // (which would need a real SaleId); it only shows the
            // "Browse products" CTA.
            //
            // If budgetInfo is null (staff user / CountOnly mode / no group),
            // the CartBudgetBar is silently hidden — the empty-cart CTA is
            // shown on its own.
            return Result<CartDto?>.Success(new CartDto { BudgetInfo = budgetInfo });
        }

        // ------------------------------------------------------------------
        // 2. Enrich with live stock. We need Product.StockQuantity for each
        //    line so the UI can:
        //      - show "Only N in stock" warning when stock < quantity
        //      - show "Out of stock" badge when stock == 0
        //      - clamp the qty selector's Max to max(stock, currentQty)
        //      - disable Submit if any line exceeds stock
        //
        //    Single round-trip via GetByIdsAsync (added to IProductRepository
        //    for this purpose). Missing products (defensive — shouldn't happen
        //    since the line was added when the product existed) are treated
        //    as stock = 0 with a log warning, so the UI shows "out of stock"
        //    and the user can remove the line.
        // ------------------------------------------------------------------
        var productIds = sale.LineItems.Select(li => li.ProductId).Distinct().ToList();
        var products = await productRepository.GetByIdsAsync(productIds, cancellationToken);
        var stockByProductId = products.ToDictionary(p => p.Id, p => p.StockQuantity);

        foreach (var productId in productIds)
        {
            if (!stockByProductId.ContainsKey(productId))
            {
                logger.LogWarning
                    ("GetActiveCartForUser: product {ProductId} on sale {SaleId} no longer exists " + "(line will appear as out-of-stock in the cart).",
                    productId, sale.Id);
            }
        }

        // ------------------------------------------------------------------
        // 2b. Resolve the current caller's per-product purchase limits in
        //     the SAME pass we already made for stock. The Product aggregate
        //     owns the per-group limit value objects; we just look them up
        //     by the caller's GroupId.
        //
        //     WHY LOAD FROM DB (not from the auth-cookie claim):
        //       The GroupId claim is a snapshot from login time and goes
        //       stale when an admin assigns the user to a group after
        //       they're already logged in. Reading from the DB guarantees
        //       the limit reflects the user's CURRENT group, so the cart's
        //       MyPurchaseLimit (used by the qty selector's clamping) is
        //       always correct without requiring a re-login.
        //
        //       This matches the fix in GetProductsPaginatedQueryHandler
        //       and CreateOrAppendSaleCommandHandler. The three handlers
        //       that previously read currentUser.GroupName (claim) are now
        //       consistent with UpdateSaleLineItemCommandHandler and
        //       AddItemToSaleCommandHandler, which already loaded the
        //       customer from the DB.
        // ------------------------------------------------------------------
        var freshUser = await userRepository.GetByIdAsync(currentUser.UserId, cancellationToken);
        var groupId = freshUser?.GroupId;
        var productById = products.ToDictionary(p => p.Id);

        int? ResolveMyLimit(Guid productId)
        {
            if (groupId is null) return null; // staff
            return productById.TryGetValue(productId, out var p)
                ? p.GetPurchaseLimitForGroup(groupId.Value)?.Limit
                : null;
        }

        // ------------------------------------------------------------------
        // 3. Project to CartDto. Line items are ordered by LineNumber for
        //    stable UI rendering — same convention as GetSaleByIdQueryHandler.
        // ------------------------------------------------------------------
        var lineItemDtos = sale.LineItems
            .OrderBy(li => li.LineNumber)
            .Select(li =>
            {
                // Resolve live stock; default to 0 if the product disappeared
                // (defensive — see the log above). The UI shows "out of stock"
                // and the user can remove the line.
                stockByProductId.TryGetValue(li.ProductId, out var currentStock);

                return new CartLineItemDto
                {
                    Id = li.Id,
                    LineNumber = li.LineNumber,
                    ProductId = li.ProductId,
                    ProductName = li.ProductName,
                    Quantity = li.Quantity,

                    UnitPrice = new MoneyDto
                    {
                        Amount = li.UnitPrice.Amount,
                        Currency = li.UnitPrice.Currency
                    },
                    LineTotal = new MoneyDto
                    {
                        // Today identical to GrossTotal (no discount/tax).
                        // When the domain gains discount/tax logic, this
                        // projection changes — see SaleLineItemDto.LineTotal.
                        Amount = li.GrossTotal.Amount,
                        Currency = li.GrossTotal.Currency
                    },

                    CurrentStock = currentStock,
                    MyPurchaseLimit = ResolveMyLimit(li.ProductId)
                };
            })
            .ToList();

        var cartDto = new CartDto
        {
            SaleId = sale.Id,
            // B2 deferred-allocation: drafts have no SaleNumber. Project to
            // null here; the DTO's DisplayNumber property computes a pseudo-id
            // (DRAFT-{Guid[0..8]}) for UI display.
            SaleNumber = sale.SaleNumber?.Value,
            TotalItemCount = lineItemDtos.Sum(li => li.Quantity),
            Total = new MoneyDto
            {
                Amount = sale.Total.Amount,
                Currency = sale.Total.Currency
            },
            LineItems = lineItemDtos,
            // Step 7 — populated for both the empty-cart + cart-with-items
            // branches so the CartBudgetBar renders in both states. See
            // the top-of-method comment for the contract semantics.
            BudgetInfo = budgetInfo
        };

        return Result<CartDto?>.Success(cartDto);
    }
}
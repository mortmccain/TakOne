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
            // Not a failure — empty cart is a normal state.
            return Result<CartDto?>.Success(null);
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

                    CurrentStock = currentStock
                };
            })
            .ToList();

        var cartDto = new CartDto
        {
            SaleId = sale.Id,
            SaleNumber = sale.SaleNumber.Value,
            TotalItemCount = lineItemDtos.Sum(li => li.Quantity),
            Total = new MoneyDto
            {
                Amount = sale.Total.Amount,
                Currency = sale.Total.Currency
            },
            LineItems = lineItemDtos
        };

        return Result<CartDto?>.Success(cartDto);
    }
}
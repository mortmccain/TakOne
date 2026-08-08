using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Products.Queries.GetProductById;

/// <summary>
/// Handler for <see cref="GetProductByIdQuery"/>.
///
/// NOTE on purchase-limit visibility: see the query file. The handler strips
/// purchase limits from the DTO unless the caller is Admin/Manager. This is
/// the single source of truth — the UI does NOT need to repeat this check.
/// </summary>
public sealed class GetProductByIdQueryHandler
{
    public static async Task<Result<ProductDto>> HandleAsync
        (
        GetProductByIdQuery query,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        ILogger<GetProductByIdQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetProductById: unauthenticated call rejected.");

            return Result<ProductDto>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the product. Purchase limits are stored as an owned
        //    collection on Product (EF Core maps them to a detail table),
        //    so GetByIdAsync includes them automatically.
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdAsync(query.ProductId, cancellationToken);

        if (product is null)
        {
            logger.LogInformation
                ("GetProductById: product {ProductId} not found. Requested by user {UserId}.",
                query.ProductId, currentUser.UserId);

            return Result<ProductDto>.Failure
                ($"Product '{query.ProductId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Determine whether the caller may see purchase limits. Customers
        //    see ONLY their own limit (if any); to keep this endpoint simple
        //    and avoid leaking other groups' limits, we strip the entire
        //    collection for non-staff callers. Staff roles (Admin, Manager,
        //    Employee) all need to see + edit limits on the ProductDetail
        //    page, so all three are granted visibility here. ReadOnly is
        //    intentionally excluded — they can view but not edit, and the
        //    limits are an internal staff detail that doesn't help them.
        // ------------------------------------------------------------------
        var canSeeAllLimits =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager) ||
            currentUser.IsInRole(Roles.Employee);

        var dto = new ProductDto
        {
            Id = product.Id,
            Name = product.Name,
            Description = product.Description,
            PictureUrl = product.PictureUrl,

            Price = new MoneyDto
            {
                Amount = product.Price.Amount,
                Currency = product.Price.Currency
            },

            StockQuantity = product.StockQuantity,

            CategoryId = product.CategoryId,
            SubCategoryId = product.SubCategoryId,
            SubSubCategoryId = product.SubSubCategoryId,

            PurchaseLimits = canSeeAllLimits
                ? product.PurchaseLimits
                    .Select
                    (
                    pl => new
                    ProductPurchaseLimitDto
                    {
                        GroupName = pl.GroupName,
                        Limit = pl.Limit
                    }
                    )
                    .ToList()
                : new List<ProductPurchaseLimitDto>(),

            // MyPurchaseLimit — the CURRENT caller's own per-group limit on
            // this product. Null for staff (no GroupName → no cap) and for
            // customers whose group has no specific limit set on this product.
            // The ProductDetail page uses this to clamp the quantity selector
            // so the customer cannot even SELECT more than their group allows.
            MyPurchaseLimit = !string.IsNullOrWhiteSpace(currentUser.GroupName)
                ? product.GetPurchaseLimitForGroup(currentUser.GroupName)?.Limit
                : null
        };

        return Result<ProductDto>.Success(dto);
    }
}
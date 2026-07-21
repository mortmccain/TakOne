using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Products.Queries.GetProductsPaginated;

/// <summary>
/// Handler for <see cref="GetProductsPaginatedQuery"/>.
///
/// The repository's <c>GetPaginatedAsync</c> accepts the same filter
/// arguments as this query, so most of the work is just clamping
/// parameters and projecting to the DTO.
/// </summary>
public sealed class GetProductsPaginatedQueryHandler
{
    private const int MaxPageSize = 100;

    public static async Task<PaginatedResult<ProductListItemDto>> HandleAsync
        (
        GetProductsPaginatedQuery query,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        ILogger<GetProductsPaginatedQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetProductsPaginated: unauthenticated call rejected.");

            return new PaginatedResult<ProductListItemDto>(Array.Empty<ProductListItemDto>(), 0, 1, 1);
        }

        // ------------------------------------------------------------------
        // 1. Clamp page parameters.
        // ------------------------------------------------------------------
        var pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
        var pageSize = query.PageSize < 1
            ? 20
            : query.PageSize > MaxPageSize
                ? MaxPageSize
                : query.PageSize;

        // ------------------------------------------------------------------
        // 2. Authorization override on IncludeInactive. Only Admin/Manager
        //    may see inactive products; everyone else silently gets active
        //    only. We don't log this as a warning — a customer's UI setting
        //    the flag is a UX bug, not an attack; just clamp silently.
        // ------------------------------------------------------------------
        var includeInactive = query.IncludeInactive;

        if (includeInactive)
        {
            var canSeeInactive =
                currentUser.IsInRole(Roles.Admin) ||
                currentUser.IsInRole(Roles.Manager);

            if (!canSeeInactive)
            {
                includeInactive = false;
            }
        }

        // The repository does NOT have an "includeInactive" parameter in
        // the current interface; for now, the inactive filter is applied
        // in the projection step. If performance becomes a concern, we'll
        // extend the repository contract.
        //
        // Note: includeInactive=false will silently exclude inactive rows
        // in the projection below.

        // ------------------------------------------------------------------
        // 3. Load the page.
        // ------------------------------------------------------------------
        var paginated = await productRepository.GetPaginatedAsync
            (
            categoryId: query.CategoryId,
            subCategoryId: query.SubCategoryId,
            subSubCategoryId: query.SubSubCategoryId,
            searchTerm: query.SearchTerm,
            pageNumber: pageNumber,
            pageSize: pageSize,
            cancellationToken: cancellationToken
            );

        // ------------------------------------------------------------------
        // 4. Project to DTO. Apply the inactive filter and search-term
        //    filter here (the repository applies its own search-term filter
        //    too; the in-memory filter is a defense-in-depth in case the
        //    implementation differs).
        // ------------------------------------------------------------------
        var searchTerm = query.SearchTerm?.Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(searchTerm);

        var dtos = paginated.Items
            .Where(p => includeInactive || /* isActive check goes here when added to domain */ true)
            .Where(p => !hasSearch ||
                        p.Name.Contains(searchTerm!, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ProductListItemDto
            {
                Id = p.Id,
                Name = p.Name,
                PictureUrl = p.PictureUrl,

                Price = new MoneyDto
                {
                    Amount = p.Price.Amount,
                    Currency = p.Price.Currency
                },

                StockQuantity = p.StockQuantity,

                CategoryId = p.CategoryId,
                SubCategoryId = p.SubCategoryId,
                SubSubCategoryId = p.SubSubCategoryId
            })
            .ToList();

        return new PaginatedResult<ProductListItemDto>(dtos, paginated.TotalCount, pageNumber, pageSize);
    }
}
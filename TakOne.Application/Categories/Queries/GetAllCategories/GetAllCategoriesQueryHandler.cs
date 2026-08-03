using Microsoft.Extensions.Logging;
using TakOne.Application.Categories.DTOs;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Queries.GetAllCategories;

/// <summary>
/// Handler for <see cref="GetAllCategoriesQuery"/>.
///
/// Mirrors <see cref="GetActiveCategoriesQueryHandler"/> but loads ALL
/// categories (active + inactive) via <see cref="ICategoryRepository.GetAllAsync"/>.
/// Used by the admin Categories management page so deactivated nodes stay
/// visible (rendered with a red outline + an "activate" toggle) instead of
/// vanishing from the page.
/// </summary>
public sealed class GetAllCategoriesQueryHandler
{
    public static async Task<Result<List<CategoryDto>>> HandleAsync
        (
        GetAllCategoriesQuery query,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        ILogger<GetAllCategoriesQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check. The page-level [Authorize(Roles=...)]
        //    attribute on AdminCategories.razor is the primary gate, but we
        //    check here too in case the query is ever invoked from a non-Blazor
        //    surface (e.g. a future API endpoint).
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetAllCategories: unauthenticated call rejected.");

            return Result<List<CategoryDto>>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load ALL categories (active + inactive) with hierarchy.
        //    The repository returns them ordered "active first, then
        //    inactive, each group sorted by Name". We preserve that order
        //    at the top level so the admin's "live" tree sits at the top
        //    of the page, with deactivated nodes grouped below for easy
        //    review / reactivation.
        // ------------------------------------------------------------------
        var categories = await categoryRepository.GetAllAsync(cancellationToken);

        // ------------------------------------------------------------------
        // 2. Project to DTOs. Sort children at each sub-level by
        //    "active first, then name" so the same visual ordering
        //    invariant holds at every depth of the tree. The top-level
        //    order is preserved as-is from the repository.
        // ------------------------------------------------------------------
        var dtos = categories
            .Select
            (
            c => new CategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                IsActive = c.IsActive,

                SubCategories = c.SubCategories
                    .OrderBy(sc => sc.IsActive ? 0 : 1)
                        .ThenBy(sc => sc.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(sc => new SubCategoryDto
                    {
                        Id = sc.Id,
                        CategoryId = sc.CategoryId,
                        Name = sc.Name,
                        IsActive = sc.IsActive,

                        SubSubCategories = sc.SubSubCategories
                            .OrderBy(ssc => ssc.IsActive ? 0 : 1)
                                .ThenBy(ssc => ssc.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(ssc => new SubSubCategoryDto
                            {
                                Id = ssc.Id,
                                SubCategoryId = ssc.SubCategoryId,
                                Name = ssc.Name,
                                IsActive = ssc.IsActive
                            })
                            .ToList()
                    })
                    .ToList()
            }
            )
            .ToList();

        return Result<List<CategoryDto>>.Success(dtos);
    }
}
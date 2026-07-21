using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Categories.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Queries.GetActiveCategories;

/// <summary>
/// Handler for <see cref="GetActiveCategoriesQuery"/>.
///
/// The repository's <c>GetAllActiveAsync</c> returns a List of Category
/// aggregates. Each Category's SubCategories / SubSubCategories collection
/// may or may not be eagerly loaded depending on the Infrastructure
/// implementation — we project defensively here (the .Select will work
/// whether the children are loaded or not, because EF Core will lazy-load
/// them on access; if you want to guarantee single round-trip, configure
/// an Include in the Infrastructure implementation).
/// </summary>
public sealed class GetActiveCategoriesQueryHandler
{
    public static async Task<Result<List<CategoryDto>>> HandleAsync
        (
        GetActiveCategoriesQuery query,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        ILogger<GetActiveCategoriesQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check. The shop page calls this endpoint
        //    on every page load, so an auth failure here is rare but we
        //    check anyway.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetActiveCategories: unauthenticated call rejected.");

            return Result<List<CategoryDto>>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load all active categories. The repository's contract says
        //    "active" means IsActive == true at the Category level. The
        //    domain's cascade-deactivation rule ensures that if a Category
        //    is active, all its child SubCategories and SubSubCategories
        //    are also active — so we don't need to filter them again here.
        // ------------------------------------------------------------------
        var categories = await categoryRepository.GetAllActiveAsync(cancellationToken);

        // ------------------------------------------------------------------
        // 2. Project to DTOs. Sort by name at each level for stable UI
        //    rendering. The shop tree control relies on the API returning
        //    a stable order so its expand/collapse state doesn't jump.
        // ------------------------------------------------------------------
        var dtos = categories
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select
            (
            c => new CategoryDto
            {
                Id = c.Id,
                Name = c.Name,
                IsActive = c.IsActive,

                SubCategories = c.SubCategories
                    .OrderBy(sc => sc.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(sc => new SubCategoryDto
                    {
                        Id = sc.Id,
                        CategoryId = sc.CategoryId,
                        Name = sc.Name,
                        IsActive = sc.IsActive,

                        SubSubCategories = sc.SubSubCategories
                            .OrderBy(ssc => ssc.Name, StringComparer.OrdinalIgnoreCase)
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
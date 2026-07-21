using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Categories.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Queries.GetCategoryByIdWithHierarchy;

/// <summary>
/// Handler for <see cref="GetCategoryByIdWithHierarchyQuery"/>.
///
/// Uses <see cref="ICategoryRepository.GetByIdWithHierarchyAsync"/> to eager-
/// load SubCategories and SubSubCategories in one DB round-trip. If we used
/// <c>GetByIdAsync</c> instead, EF Core's lazy loading would issue N+1
/// queries when we iterated over <c>category.SubCategories</c>.
/// </summary>
public sealed class GetCategoryByIdWithHierarchyQueryHandler
{
    public static async Task<Result<CategoryDto>> HandleAsync
        (
        GetCategoryByIdWithHierarchyQuery query,
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepository,
        ILogger<GetCategoryByIdWithHierarchyQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check. Category reads are allowed for
        //    all authenticated users (they're needed for the shop), so no
        //    role check follows — just the auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetCategoryByIdWithHierarchy: unauthenticated call rejected.");

            return Result<CategoryDto>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the category with its full hierarchy.
        // ------------------------------------------------------------------
        var category = await categoryRepository.GetByIdWithHierarchyAsync(query.CategoryId, cancellationToken);

        if (category is null)
        {
            logger.LogInformation
                ("GetCategoryByIdWithHierarchy: category {CategoryId} not found. Requested by user {UserId}.",
                query.CategoryId, currentUser.UserId);

            return Result<CategoryDto>.Failure
                ($"Category '{query.CategoryId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Project the full hierarchy to DTOs. Order sub-categories and
        //    sub-sub-categories by name for stable UI rendering (the domain
        //    stores them in insertion order, which isn't user-friendly).
        // ------------------------------------------------------------------
        var dto = new CategoryDto
        {
            Id = category.Id,
            Name = category.Name,
            IsActive = category.IsActive,

            SubCategories = category.SubCategories
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
        };

        return Result<CategoryDto>.Success(dto);
    }
}
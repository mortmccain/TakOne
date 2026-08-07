using TakOne.Application.Categories.DTOs;
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Queries.GetCategoryByIdWithHierarchy;

/// <summary>
/// Loads a single Category by Id with its full hierarchy of SubCategories
/// and SubSubCategories eagerly loaded, and projects it to
/// <see cref="CategoryDto"/>.
///
/// USED BY:
///   - Admin category-management page (full edit view).
///   - Shop category-tree navigation (renders the tree client-side).
///
/// NOTE on visibility: categories don't have an "inactive customer can't
/// see it" rule. Inactive categories are returned by this query so the admin
/// page can show them; the shop page uses <c>GetActiveCategories</c> instead,
/// which filters inactive out at the repository level.
/// </summary>
[RequireAuthentication]
public sealed class GetCategoryByIdWithHierarchyQuery
{
    public Guid CategoryId { get; init; }
}
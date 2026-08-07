using TakOne.Application.Categories.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Queries.GetAllCategories;

/// <summary>
/// Loads ALL Categories (active AND inactive) with their full hierarchy
/// of SubCategories and SubSubCategories, and returns them as a flat list
/// of <see cref="CategoryDto"/>.
///
/// USED BY:
///   - The admin Categories management page (AdminCategories.razor) ONLY.
///     The admin needs to see deactivated categories so they can be
///     reactivated — they must NOT vanish from the page just because
///     they were deactivated. The UI renders inactive nodes with a red
///     outline + an "activate" toggle button.
///
/// NOT USED BY:
///   - The shop sidebar / product-create / product-edit pickers — those
///     use <see cref="GetActiveCategoriesQuery"/> so customers never see
///     deactivated categories in navigation.
///
/// AUTHORIZATION:
///   The handler checks that the caller is authenticated, but role-based
///   access control is enforced at the Razor page level via
///   <c>[Authorize(Roles = "Admin,Manager")]</c> on AdminCategories.razor.
///   Defense-in-depth: the handler still rejects unauthenticated calls.
///
/// ORDERING:
///   The repository returns categories ordered "active first, then
///   inactive, each group sorted by Name". The handler preserves that
///   order and ALSO sorts the children at each level by Name (active
///   first within each parent) so the rendered tree is stable and
///   predictable.
/// </summary>
[RequireRoles(Roles.Admin, Roles.Manager)]
public sealed class GetAllCategoriesQuery
{
    // No parameters — this is a "load the whole tree, including
    // deactivated nodes" query. The admin page is the only caller, and
    // it always wants the full tree.
    //
    // We intentionally do NOT add a paging parameter here. The category
    // tree is small enough to fit in one page, and the UI renders it as
    // a single expandable tree. If we ever need paging, we'll add a
    // separate GetCategoriesPaginatedQuery.
}
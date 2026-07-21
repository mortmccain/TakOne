using TakOne.Application.Categories.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Categories.Queries.GetActiveCategories;

/// <summary>
/// Loads ALL active Categories with their full hierarchy of active
/// SubCategories and SubSubCategories, and returns them as a flat list of
/// <see cref="CategoryDto"/>.
///
/// USED BY:
///   - The shop's category tree (customer-facing). Inactive categories are
///     excluded — they should not appear in navigation.
///   - The product-create / product-edit page's category picker (so users
///     can't assign a product to a deactivated category).
///
/// PERFORMANCE:
///   This query returns the entire active category tree in one shot. For
///   TakOne's expected scale (tens to low hundreds of categories), this is
///   fine. If the catalog ever grows to thousands of categories, we'll add
///   a paginated variant or a lazy-load-per-level endpoint.
///
/// NOTE: this query returns ALL active categories. There is no per-user
/// filtering — categories are public to all authenticated users.
/// </summary>
public sealed class GetActiveCategoriesQuery
{
    // No parameters — this is a "load the whole tree" query.
    //
    // We intentionally do NOT add a paging parameter here. The category tree
    // is small enough to fit in one page, and the UI renders it as a single
    // expandable tree. If we ever need paging, we'll add a separate
    // GetCategoriesPaginatedQuery (which would also include inactive
    // categories for the admin view).
}
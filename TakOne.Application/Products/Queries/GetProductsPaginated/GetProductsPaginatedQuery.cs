using TakOne.Application.Common.Authorization;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Queries.GetProductsPaginated;

/// <summary>
/// Paginated list query for Products. Returns
/// <see cref="PaginatedResult{ProductListItemDto}"/>.
///
/// FILTERS:
///   - <see cref="CategoryId"/> / <see cref="SubCategoryId"/> / <see cref="SubSubCategoryId"/>
///     cascade (a CategoryId filter returns products in that category OR
///     any of its sub/subsub categories). The repository enforces the
///     hierarchy — if SubCategoryId is set, CategoryId is implied.
///   - <see cref="SearchTerm"/> is matched case-insensitively against Name.
///   - <see cref="IncludeInactive"/> defaults to false. Only Admin/Manager
///     callers should set it to true (the handler enforces this — see below).
///
/// AUTHORIZATION:
///   - If <see cref="IncludeInactive"/> is true but the caller is not
///     Admin/Manager, the handler silently treats it as false. This is
///     defense-in-depth: even if a malicious client sets the flag, the
///     server-side check still wins.
///
/// ROUND 6 (server-driven paging for the AdminProducts grid — mirrors the
/// Round-5 AdminUsers conversion): the grid's per-column filters and sorts
/// now travel WITH the query and are evaluated in SQL:
///   - <see cref="NameFilter"/>: typed text filter (operator + value) for
///     the Name column.
///   - <see cref="StockStatus"/>: the Status dropdown's In/Out-of-stock
///     filter.
///   - <see cref="PriceFilter"/> / <see cref="StockFilter"/>: numeric
///     comparison filters for the Price and Stock columns.
///   - <see cref="CategoryNameFilter"/> / <see cref="SubCategoryNameFilter"/> /
///     <see cref="SubSubCategoryNameFilter"/>: text filters on the RESOLVED
///     category display names. The handler resolves each against the
///     category tree it already loads for DTO enrichment and pushes the
///     matched Id sets into SQL (see <see cref="ProductsListFilters.CategoryIds"/>
///     for why the Id-set form — the product row stores only Guid FKs).
///     This keeps the grid's existing free-text filter UX; the alternative
///     (a Guid-keyed dropdown like AdminUsers' group filter) was rejected
///     because that page already HAD a dropdown, while this page's category
///     columns are text filters today.
///   - <see cref="SortDescending"/>: direction for <see cref="SortBy"/>
///     (needed for the Name column's descending order; see
///     <see cref="ProductSortBy"/> for why the price/stock keys encode
///     their direction).
///   All new members are optional and null-defaulting, so the query stays
///   source-compatible with its other callers (the shop pages, the mobile
///   catalog, and the mobile search typeahead).
/// </summary>
[RequireAuthentication]
public sealed class GetProductsPaginatedQuery
{
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    public string? SearchTerm { get; init; }

    public Guid? CategoryId { get; init; }
    public Guid? SubCategoryId { get; init; }
    public Guid? SubSubCategoryId { get; init; }

    /// <summary>
    /// If true, includes inactive (deactivated) products in the result.
    /// Defaults to false. The handler will force this to false for non-admin
    /// callers regardless of what the client requested.
    /// </summary>
    public bool IncludeInactive { get; init; }

    /// <summary>
    /// Optional catalog sort (Round 4 — shop sorting). Null = the
    /// default alphabetical-by-name order (the pre-Round-4 behavior);
    /// see <see cref="ProductSortBy"/> for the available orders and why
    /// "newest" is not among them.
    /// </summary>
    public ProductSortBy? SortBy { get; init; }

    /// <summary>
    /// Sort direction for <see cref="SortBy"/> (Round 6). Ignored when
    /// SortBy is null (the default order is Name ascending, period — the
    /// handler normalizes so a stray SortDescending can never flip the
    /// default).
    /// </summary>
    public bool SortDescending { get; init; }

    /// <summary>
    /// Typed text filter for the Name column (Round 6) — operator +
    /// value, evaluated in SQL. Null = no filter.
    /// </summary>
    public ProductsTextFilter? NameFilter { get; init; }

    /// <summary>
    /// The Status column's dropdown filter (Round 6): In stock
    /// (StockQuantity &gt; 0) or Out of stock (== 0). Null = All. Composes
    /// with <see cref="StockFilter"/> — the grid has both a Stock numeric
    /// filter and this status dropdown, and both are AND-ed in SQL.
    /// </summary>
    public ProductStockStatus? StockStatus { get; init; }

    /// <summary>
    /// Numeric comparison filter on <c>Price.Amount</c> (Round 6).
    /// Null = no filter.
    /// </summary>
    public ProductsNumberFilter? PriceFilter { get; init; }

    /// <summary>
    /// Numeric comparison filter on <c>StockQuantity</c> (Round 6).
    /// Null = no filter.
    /// </summary>
    public ProductsNumberFilter? StockFilter { get; init; }

    /// <summary>
    /// Text filter on the RESOLVED top-level category display name
    /// (Round 6). The handler resolves the term against the category tree
    /// and pushes the matched Id set into SQL. Null = no filter.
    /// </summary>
    public ProductsTextFilter? CategoryNameFilter { get; init; }

    /// <summary>
    /// Text filter on the resolved sub-category display name (Round 6) —
    /// resolved to a SubCategoryId set by the handler. Null = no filter.
    /// </summary>
    public ProductsTextFilter? SubCategoryNameFilter { get; init; }

    /// <summary>
    /// Text filter on the resolved sub-sub-category display name
    /// (Round 6) — resolved to a SubSubCategoryId set by the handler.
    /// Null = no filter.
    /// </summary>
    public ProductsTextFilter? SubSubCategoryNameFilter { get; init; }
}

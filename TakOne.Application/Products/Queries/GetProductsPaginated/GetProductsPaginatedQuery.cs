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
}
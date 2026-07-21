using TakOne.Application.Sales.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Queries.GetSalesPaginated;

/// <summary>
/// Paginated list query for Sales. Returns <see cref="PaginatedResult{SaleListItemDto}"/>,
/// which includes total count + page metadata so the UI can render pagination
/// controls.
///
/// FILTERING MODEL:
///   - Admins/Managers/Employees: see ALL sales (filtered only by SearchTerm
///     if provided).
///   - Everyone else (customers, read-only): see only their own sales.
///
///   The handler decides this by inspecting ICurrentUserService.IsInRole(...)
///   and either passes an empty specification (all sales) or a
///   <c>SaleByCreatorSpecification</c> scoped to the current user.
///
/// SEARCH TERM:
///   Matched case-insensitively against SaleNumber and CustomerName.
///   Null/whitespace means "no filter".
/// </summary>
public sealed class GetSalesPaginatedQuery
{
    /// <summary>
    /// 1-based page number. Defaults to 1.
    /// </summary>
    public int PageNumber { get; init; } = 1;

    /// <summary>
    /// Page size. Defaults to 20. Capped server-side at 100 to prevent
    /// accidental huge queries (the handler clamps anything above 100).
    /// </summary>
    public int PageSize { get; init; } = 20;

    /// <summary>
    /// Optional case-insensitive search against SaleNumber and CustomerName.
    /// </summary>
    public string? SearchTerm { get; init; }

    // NOTE: no FilterByCreatorId on the query object — the handler resolves
    // the current user's id from ICurrentUserService so callers can't snoop
    // on other users' sales by passing a foreign Guid.
}
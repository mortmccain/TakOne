using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Queries.GetSalesPaginated;

/// <summary>
/// Paginated list query for Sales. Returns <see cref="PaginatedResult{SaleListItemDto}"/>,
/// which includes total count + page metadata so the UI can render pagination
/// controls.
///
/// FILTERING MODEL:
///   - Admins/Managers/Employees: see ALL sales (filtered only by SearchTerm
///     and Status if provided).
///   - Everyone else (customers, read-only): see only their own sales
///     (further filtered by SearchTerm and Status if provided).
///
///   The handler decides this by inspecting ICurrentUserService.IsInRole(...)
///   and either passes an empty specification (all sales) or a
///   <c>SaleByCustomerSpecification</c> scoped to the current user. The
///   optional <see cref="Status"/> filter is pushed down to SQL via the
///   specification (Phase 7 item E — was previously filtered client-side
///   on the materialized page, which doesn't scale beyond one page).
///
/// SEARCH TERM:
///   Matched case-insensitively against SaleNumber and CustomerName.
///   Null/whitespace means "no filter".
///
/// STATUS:
///   Optional <see cref="SaleStatus"/> filter. When null, all statuses are
///   returned. When set, only sales in the given status are returned. The
///   filter is applied in SQL (via the specification), not in-memory, so
///   pagination TotalCount is accurate.
/// </summary>
[RequireAuthentication]
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

    /// <summary>
    /// Optional status filter. When null, all statuses are returned. When
    /// set, only sales in the given status are returned. Pushed down to SQL
    /// via the specification (Phase 7 item E).
    /// </summary>
    public SaleStatus? Status { get; init; }

    /// <summary>
    /// Optional INCLUSIVE lower bound on the sale's creation time, as a UTC
    /// instant. When non-null, only sales created ON OR AFTER this instant
    /// are returned. Applied in SQL via the specification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>TIMEZONE CONTRACT</b>: the bound is a raw UTC instant — the
    /// server never applies an implicit offset, so the same query means
    /// the same rows regardless of the server's or caller's locale. The
    /// UI (which renders CreatedAtUtc in Tehran time, +03:30 with no DST
    /// since 2022) converts the picked LOCAL date to UTC before
    /// dispatching: local midnight → UTC = localDate − 3:30. See
    /// Sales.razor's ToUtcInstant helper.
    /// </para>
    /// </remarks>
    public DateTime? FromDateUtc { get; init; }

    /// <summary>
    /// Optional EXCLUSIVE upper bound on the sale's creation time, as a
    /// UTC instant. When non-null, only sales created STRICTLY BEFORE
    /// this instant are returned. Applied in SQL via the specification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exclusive upper + inclusive lower = the canonical half-open
    /// interval <c>[from, to)</c>: consecutive ranges tile perfectly with
    /// no gaps and no double-counted midnights, and "through Aug 29"
    /// is expressed as "before Aug 30 local midnight" — every sale
    /// placed any time ON Aug 29 is included with no time-of-day
    /// guesswork.
    /// </para>
    /// </remarks>
    public DateTime? ToDateUtc { get; init; }

    // NOTE: no FilterByCreatorId on the query object — the handler resolves
    // the current user's id from ICurrentUserService so callers can't snoop
}
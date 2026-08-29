using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Sales.Specifications;

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
///   Matched case-insensitively against SaleNumber (via the smart
///   number-term parser — see <see cref="SaleNumberSearchParser"/>) OR
///   CustomerName, as one server-side OR predicate. Null/whitespace
///   means "no filter". Round 4 moved this from an in-memory
///   post-pagination filter to SQL, so it now matches across ALL pages
///   (previously it only filtered within the loaded page — MobileSearch
///   silently only searched the newest rows).
///
/// STATUS:
///   Optional <see cref="SaleStatus"/> filter. When null, all statuses are
///   returned. When set, only sales in the given status are returned. The
///   filter is applied in SQL (via the specification), not in-memory, so
///   pagination TotalCount is accurate.
///
/// COLUMN FILTERS + SORT (Round 4 — server-driven paging):
///   The desktop Sales grid runs in Radzen LoadData mode: every sort
///   click, column-filter change, and pager page change re-dispatches
///   this query. The WebUI layer translates the grid's filter/sort
///   descriptors into the typed filter records below
///   (<see cref="SalesTextFilter"/>, <see cref="SalesAmountFilter"/>,
///   <see cref="SalesSortBy"/>); the handler packs them into a
///   <see cref="SalesListFilters"/> for the specifications, which push
///   them into SQL so TotalCount is accurate for EVERY active filter.
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

    /// <summary>
    /// Optional filter term for the sale-number column (Round 4).
    /// Parsed server-side into Year/Sequence/draft predicates — see
    /// <see cref="SaleNumberSearchParser"/> for the supported term
    /// shapes. Null/whitespace = no filter.
    /// </summary>
    public string? SaleNumberTerm { get; init; }

    /// <summary>
    /// Optional customer-name column filter (Round 4). The WebUI layer
    /// translates the grid's filter descriptor (term + operator) into
    /// this typed record.
    /// </summary>
    public SalesTextFilter? CustomerNameFilter { get; init; }

    /// <summary>
    /// Optional creator-name column filter (Round 4), staff-only
    /// column — the handler does NOT enforce role-gating on the filter
    /// itself (a customer filtering by creator name can only ever
    /// match their own sales anyway, thanks to the customer-scoped
    /// specification).
    /// </summary>
    public SalesTextFilter? CreatedByNameFilter { get; init; }

    /// <summary>
    /// Optional total-amount column filter (Round 4): a comparison
    /// operator + operand applied to the sale total's raw decimal
    /// amount (currency-blind by design — the grid column filters on
    /// the underlying amount).
    /// </summary>
    public SalesAmountFilter? TotalFilter { get; init; }

    /// <summary>
    /// Optional sort key (Round 4). Null = no user sort active → the
    /// specification defaults to newest-first (CreatedAtUtc DESC).
    /// </summary>
    public SalesSortBy? SortBy { get; init; }

    /// <summary>
    /// Sort direction for <see cref="SortBy"/> (Round 4). Also applies
    /// to the default sort when <see cref="SortBy"/> is null (the
    /// desktop grid never dispatches a null sort with descending=false,
    /// but the query contract keeps the two orthogonal).
    /// </summary>
    public bool SortDescending { get; init; } = true;

    // NOTE: no FilterByCreatorId on the query object — the handler resolves
    // the current user's id from ICurrentUserService so callers can't snoop
}
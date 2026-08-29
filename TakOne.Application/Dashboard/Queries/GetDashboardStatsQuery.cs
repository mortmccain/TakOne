using TakOne.Application.Common.Authorization;
namespace TakOne.Application.Dashboard.Queries.GetDashboardStats;

/// <summary>
/// Query for the Dashboard page's KPI cards + revenue charts + status donut.
///
/// AUTHORIZATION MODEL:
///   The dashboard is staff-only. Customers never see this query — they're
///   redirected to /Products at login time (roadmap Section 12.8), and the
///   Dashboard.razor page is gated by <c>[Authorize]</c>.
///
///   The handler also re-checks authentication + role as defense-in-depth
///   (Wolverine's AuthorizationMiddleware is the first line, but tests or
///   future hosts could bypass it).
///
/// ROLE-BASED SCOPING (roadmap Section 12.2 — Option D):
///   - Admin / Manager / ReadOnly → see ALL sales (company-wide overview)
///   - Employee → see ONLY the sales they personally approved
///   - Customer → rejected with "Access denied" (defense-in-depth)
///
/// WHY THE QUERY DOES NOT CARRY UserRoles (Brutal Code Review v3 #03):
///   The previous design exposed <c>UserRoles</c> as a public mutable
///   setter on the query DTO. The handler trusted
///   <c>query.UserRoles.Contains(Roles.Customer)</c> to scope the data.
///   This was a CRITICAL security hole: any authenticated caller could
///   dispatch the query with <c>UserRoles = new[] { Roles.Admin }</c>
///   and read the entire company-wide dashboard — all sales, all revenue,
///   every employee's performance. The role list came from the
///   UNAUTHENTICATED caller's DTO, not from the server's verified claims.
///
///   The handler ALREADY injects <c>ICurrentUserService</c> for the
///   UserId (used by <c>SaleByApproverSpecification</c>) and the FullName
///   (used in the welcome header). Reading roles from
///   <c>ICurrentUserService.IsInRole</c> — which the Infrastructure layer
///   sources from the server-verified claims — closes the spoofing hole
///   and is strictly more correct. There is no testability trade-off:
///   <c>ICurrentUserService</c> is a thin interface that tests can stub.
/// </summary>
[RequireRoles(Roles.Admin, Roles.Manager, Roles.Employee, Roles.ReadOnly)]
public sealed class GetDashboardStatsQuery
{
    // The query carries NO caller-identity fields. All role/userId
    // resolution comes from the handler-injected ICurrentUserService,
    // which reads server-verified claims. This is the fix for Brutal
    // Code Review v3 finding #03.
    //
    // The DTO intentionally stayed parameter-free through Round 4: the
    // dashboard was a fixed-scope query (the caller's identity determines
    // the slice, but no caller-supplied parameters filter the result
    // set). Round 5 adds the PERIOD SELECTOR — the two date members
    // below — as { get; init; } properties per this comment's own
    // long-standing invitation ("If future requirements add date-range
    // or category filters, add them here as { get; init; } properties —
    // never { get; set; }").

    /// <summary>
    /// ROUND 5 — PERIOD SELECTOR. Optional inclusive lower bound of a
    /// half-open UTC interval [FromUtc, ToUtc) that re-anchors the
    /// dashboard's three flow-type KPI cards (orders, employee purchase,
    /// approved+invoiced sales) and their trend deltas to the window
    /// instead of the fixed today/this-month anchors.
    ///
    /// <para>
    /// <b>WHEN NULL (the default)</b>: the handler keeps the legacy
    /// fixed-anchor behavior — TodayOrdersCount is "today",
    /// ThisMonthEmployeePurchaseTotal is "this calendar month", and the
    /// deltas compare against yesterday / the previous calendar month.
    /// All existing callers and tests are unaffected.
    /// </para>
    /// <para>
    /// <b>WHEN SET</b>: the window is [FromUtc, ToUtc) and the DELTAS
    /// compare against the immediately-preceding equal-length window
    /// [FromUtc − (ToUtc − FromUtc), FromUtc). ToUtc is optional — when
    /// null it defaults to the handler's current UtcNow. An inverted or
    /// degenerate window (FromUtc &gt;= ToUtc) yields zero-valued period
    /// KPIs (never throws — same degenerate-range semantics as the sales
    /// list's date filter).
    /// </para>
    /// <para>
    /// <b>SCOPE NOTE</b>: the period re-anchors the FLOW-TYPE KPI CARDS
    /// only. The weekly revenue chart, status donut, top products /
    /// categories / employees, recent orders, and the all-time counts
    /// keep their own (clearly labeled) scopes — parameterizing them
    /// needs the date-bucket repository aggregations noted as deferred
    /// in the handler.
    /// </para>
    /// <para>
    /// <b>TIMEZONE CONTRACT</b> (same as GetSalesPaginatedQuery): these
    /// are raw UTC instants. The UI converts picked LOCAL dates to UTC
    /// before dispatching (Tehran local midnight → UTC = localDate −
    /// 03:30; see Sales.razor's ToUtcInstant helper).
    /// </para>
    /// </summary>
    public DateTime? FromUtc { get; init; }

    /// <summary>
    /// ROUND 5 — PERIOD SELECTOR. Optional EXCLUSIVE upper bound of the
    /// half-open UTC interval [FromUtc, ToUtc). Null (with FromUtc set)
    /// means "up to now". Ignored when FromUtc is null.
    /// </summary>
    public DateTime? ToUtc { get; init; }
}

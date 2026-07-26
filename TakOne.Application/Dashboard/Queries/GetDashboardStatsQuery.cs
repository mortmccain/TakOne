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
/// WHY THE QUERY CARRIES UserRoles INSTEAD OF LETTING THE HANDLER READ THEM:
///   The ERP reference's pattern (which we adopt) is to pass the caller's
///   roles into the query so the handler doesn't have to inject
///   <c>ICurrentUserService</c> just to read roles. We DO still inject
///   <c>ICurrentUserService</c> for the UserId (used by
///   <c>SaleByApproverSpecification</c>) and the FullName (used in the
///   welcome header) — but the role list comes through the query itself.
///   This makes the query more testable (you can construct one with any
///   role combination) and matches the convention used by the existing
///   <c>GetSalesPaginatedQuery</c>.
/// </summary>
public sealed class GetDashboardStatsQuery
{
    /// <summary>
    /// The user ID of the caller. Used by the Employee-scoped branch to
    /// filter sales by <c>ApprovedByUserId</c>.
    /// </summary>
    public Guid RequestedByUserId { get; set; }

    /// <summary>
    /// The caller's role names (e.g. ["Admin"], ["Employee"]). The handler
    /// checks for "Admin", "Manager", "ReadOnly", "Employee", "Customer" by
    /// name (these strings come from <c>TakOne.Application.Common.Authorization.Roles</c>
    /// — but we accept them as plain strings here so the query stays
    /// assembly-reference-free of the Roles static class).
    /// </summary>
    public IReadOnlyList<string> UserRoles { get; set; } = Array.Empty<string>();
}
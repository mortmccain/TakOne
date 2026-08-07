using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Queries.GetAllGroupNames;

/// <summary>
/// Returns the distinct list of customer group names that currently exist
/// in the system (derived from <c>User.GroupName</c> where non-null).
///
/// Used by the CreateProduct page's per-group purchase-limit editor so
/// staff can pick from existing group names rather than typing them. Also
/// used by the AdminProducts page when editing an existing product's limits.
///
/// AUTHORIZATION (Issue #08):
///   [RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)] — staff
///   only. Customers never need to enumerate groups. The route-level
///   <c>[Authorize(Roles = "Admin,Manager,Employee")]</c> on the pages
///   that invoke this query is the first line of defense; this attribute
///   is the middleware-level backstop.
///
///   PREVIOUS WORKAROUND (NOW OBSOLETE):
///   This query deliberately did NOT use <c>[RequireRoles]</c> because
///   <c>AuthorizationMiddleware</c> read <c>ICurrentUserService.IsAuthenticated</c>,
///   which in Blazor Server relied on <c>IHttpContextAccessor.HttpContext</c> —
///   and that was <c>null</c> during <c>OnInitializedAsync</c> (after the
///   circuit was established). That workaround is no longer needed because
///   <c>BlazorCurrentUserService</c> now resolves the user from
///   <c>AuthenticationStateProvider</c> when <c>HttpContext</c> is null
///   (the Issue #08 fix). The middleware now correctly authenticates
///   circuit-initiated calls.
///
/// EMPTY RESULT:
///   An empty list is a NORMAL result — it means no customer users have
///   been created yet (or none have a GroupName assigned). The CreateProduct
///   UI should still allow staff to type a new group name freely in that
///   case (forward-looking — a limit can be set for a group before any
///   users exist in it).
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed class GetAllGroupNamesQuery
{
    // No parameters — returns the global list of distinct group names.
}

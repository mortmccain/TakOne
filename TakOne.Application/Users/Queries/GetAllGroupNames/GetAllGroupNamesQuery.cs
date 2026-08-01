namespace TakOne.Application.Users.Queries.GetAllGroupNames;

/// <summary>
/// Returns the distinct list of customer group names that currently exist
/// in the system (derived from <c>User.GroupName</c> where non-null).
///
/// Used by the CreateProduct page's per-group purchase-limit editor so
/// staff can pick from existing group names rather than typing them. Also
/// used by the AdminProducts page when editing an existing product's limits.
///
/// AUTHORIZATION:
///   Staff only (Employee/Manager/Admin) — customers never need to enumerate
///   groups. Authorization is enforced at the ROUTE level: every page that
///   invokes this query is decorated with
///   <c>[Authorize(Roles = "Admin,Manager,Employee")]</c>.
///
///   This query deliberately does NOT use <c>[RequireRoles]</c> because
///   <c>AuthorizationMiddleware</c> reads <c>ICurrentUserService.IsAuthenticated</c>,
///   which in Blazor Server relies on <c>IHttpContextAccessor.HttpContext</c> —
///   and that is <c>null</c> during <c>OnInitializedAsync</c> (after the
///   circuit is established). With <c>[RequireRoles]</c> the middleware
///   would return <c>Result.Failure("Authentication required.")</c> — a
///   non-generic <c>Result</c> — which Wolverine cannot use as the
///   <c>Result&lt;List&lt;string&gt;&gt;</c> the caller is awaiting,
///   causing the page to hang on the "LoadingGroups" placeholder.
///
/// EMPTY RESULT:
///   An empty list is a NORMAL result — it means no customer users have
///   been created yet (or none have a GroupName assigned). The CreateProduct
///   UI should still allow staff to type a new group name freely in that
///   case (forward-looking — a limit can be set for a group before any
///   users exist in it).
/// </summary>
public sealed class GetAllGroupNamesQuery
{
    // No parameters — returns the global list of distinct group names.
}

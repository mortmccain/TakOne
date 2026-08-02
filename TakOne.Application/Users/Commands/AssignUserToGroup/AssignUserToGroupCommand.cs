using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.AssignUserToGroup;

/// <summary>
/// Assigns a user to a customer group (sets <c>User.GroupName</c>).
///
/// AUTHORIZATION:
///   Employee, Manager, Admin — with role-based scope restrictions
///   enforced in the handler:
///     - Admin    → may assign any user's group.
///     - Manager  → may only assign groups for Employee or Customer users
///                  (not self, not other managers, not admins, not read-onlys).
///     - Employee → may only assign groups for Customer users
///                  (not self, not other employees, not managers/admins/read-onlys).
///   Customers do not have access to the admin pages that surface this command.
///
/// USE CASES:
///   - Creating a new customer (when CreateCustomer wasn't used, or the
///     group was wrong).
///   - Moving a customer from one group to another (purchase limits
///     change accordingly).
///   - Converting a staff user to a customer (assign group + assign
///     Customer role via AssignUserRoleCommand).
///   - Inline "Change group" action on the Admin Users page (Phase 6.5).
///
/// NOTE:
///   This command does NOT assign the Customer ASP.NET Identity role —
///   that's a separate concern (AssignUserRoleCommand). The domain
///   GroupName and the Identity role are intentionally decoupled so an
///   admin can stage changes (set group first, then assign role).
///
/// IDEMPOTENCY:
///   Assigning to the same group is allowed (the domain doesn't reject it).
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record AssignUserToGroupCommand(Guid UserId, string GroupName);
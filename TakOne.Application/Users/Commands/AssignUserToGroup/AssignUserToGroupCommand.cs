using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.AssignUserToGroup;

/// <summary>
/// Assigns a user to a customer group (sets <c>User.GroupName</c>).
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// USE CASES:
///   - Creating a new customer (when CreateCustomer wasn't used, or the
///     group was wrong).
///   - Moving a customer from one group to another (purchase limits
///     change accordingly).
///   - Converting a staff user to a customer (assign group + assign
///     Customer role via AssignUserRoleCommand).
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
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record AssignUserToGroupCommand(Guid UserId, string GroupName);
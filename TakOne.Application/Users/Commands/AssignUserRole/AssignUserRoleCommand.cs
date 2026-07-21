using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.AssignUserRole;

/// <summary>
/// Assigns an ASP.NET Identity role to a user.
///
/// AUTHORIZATION:
///   Admin only.
///
/// USE CASES:
///   - Promote an Employee to Manager.
///   - Add the Customer role to a staff user so they can buy on their
///     own behalf.
///   - Grant ReadOnly to a manager who needs view-only access to certain
///     dashboards.
///
/// IDEMPOTENCY:
///   The implementation is idempotent — assigning a role the user already
///   has returns success without re-adding.
///
/// NOTE:
///   This command does NOT change the domain User's GroupName. If you're
///   converting a staff user to a customer, you need BOTH this command
///   (assign Customer role) AND AssignUserToGroupCommand (set GroupName).
///   They're intentionally decoupled.
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed record AssignUserRoleCommand(Guid UserId, string Role);
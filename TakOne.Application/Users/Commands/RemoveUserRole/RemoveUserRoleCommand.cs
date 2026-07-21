using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.RemoveUserRole;

/// <summary>
/// Removes an ASP.NET Identity role from a user.
///
/// AUTHORIZATION:
///   Admin only.
///
/// USE CASES:
///   - Demote a Manager back to Employee.
///   - Remove the Customer role from a staff user who shouldn't be buying.
///   - Revoke ReadOnly access.
///
/// IDEMPOTENCY:
///   The implementation is idempotent — removing a role the user doesn't
///   have returns success.
///
/// SELF-REMOVAL GUARD:
///   The handler rejects attempts by an admin to remove the Admin role
///   from themselves — this would lock them out if they're the only admin.
///   (To transfer admin privileges, assign Admin to another user first,
///   THEN remove it from yourself.)
///
/// LAST-ADMIN GUARD:
///   The handler does NOT enforce "you can't remove the last Admin role
///   from the system" — that's a deeper invariant that would require
///   querying all admins. The Infrastructure layer's Identity account
///   service MAY add this guard, but the Application layer trusts the
///   admin to not shoot themselves in the foot.
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed record RemoveUserRoleCommand(Guid UserId, string Role);
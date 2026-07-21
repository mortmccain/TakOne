using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.RemoveUserFromGroup;

/// <summary>
/// Removes a user from their customer group (sets <c>User.GroupName</c> to null).
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// USE CASES:
///   - Converting a customer to staff (remove group + remove Customer role
///     via RemoveUserRoleCommand).
///   - Cleaning up before deactivation.
///
/// PURCHASE LIMITS:
///   After this call, per-product purchase limits no longer apply to the
///   user. If they still have the Customer Identity role, they can buy
///   without limits. To fully convert to staff, also remove the Customer
///   role (see RemoveUserRoleCommand).
///
/// IDEMPOTENCY:
///   Removing from group when GroupName is already null is a no-op (the
///   domain method unconditionally sets GroupName = null).
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record RemoveUserFromGroupCommand(Guid UserId);
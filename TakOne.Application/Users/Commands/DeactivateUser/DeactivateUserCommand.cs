using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.DeactivateUser;

/// <summary>
/// Soft-deletes a user by setting <c>User.IsActive</c> = false.
///
/// AUTHORIZATION:
///   Manager, Admin. (An admin can deactivate another admin — but cannot
///   deactivate themselves, see handler.)
///
/// IDEMPOTENCY:
///   Deactivating an already-deactivated user is a no-op.
///
/// WHAT THIS DOES NOT DO:
///   - Does NOT remove the user's ASP.NET Identity account. The row stays
///     for audit; the user just can't log in.
///   - Does NOT cancel the user's pending sales. Existing sales keep their
///     CustomerId reference and proceed normally.
///   - Does NOT remove the user's group membership (GroupName is kept on
///     the domain User for historical audit).
///
/// SELF-DEACTIVATION:
///   The handler rejects attempts to deactivate yourself — an admin who
///   deactivates themselves would lock themselves out, with no way back
///   without DB access.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record DeactivateUserCommand(Guid UserId);
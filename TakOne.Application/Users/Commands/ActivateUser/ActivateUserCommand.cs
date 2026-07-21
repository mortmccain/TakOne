using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.ActivateUser;

/// <summary>
/// Reactivates a previously deactivated user.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// IDEMPOTENCY:
///   Activating an already-active user is a no-op. We still persist
///   (idempotent no-op → SaveChangesAsync is a null-op round-trip).
///
/// NOTE:
///   This command only flips the domain User.IsActive flag. It does NOT
///   re-enable the user's ASP.NET Identity login (e.g. clear lockout).
///   If the user was locked out via Identity's lockout mechanism (too
///   many failed password attempts), they need a separate unlock flow
///   that lives in Infrastructure. For now, IsActive is the only
///   activation flag we model.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record ActivateUserCommand(Guid UserId);
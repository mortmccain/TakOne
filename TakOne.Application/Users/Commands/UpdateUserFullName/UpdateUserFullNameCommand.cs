using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.UpdateUserFullName;

/// <summary>
/// Updates a user's full name. Domain-only operation — does NOT touch
/// the ApplicationUser (Identity account) because UserName on ApplicationUser
/// is the WorkerId, not the FullName.
///
/// AUTHORIZATION:
///   Manager, Admin. (Users cannot rename themselves via this command —
///   self-service name changes would go through a different command with
///   weaker authorization, if we add one later.)
///
/// IDEMPOTENCY:
///   Renaming to the same name is allowed (the domain doesn't reject it).
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record UpdateUserFullNameCommand(Guid UserId, string NewFullName);
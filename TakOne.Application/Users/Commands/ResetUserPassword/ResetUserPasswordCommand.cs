using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.ResetUserPassword;

/// <summary>
/// Admin resets a user's password. The user gets a new password — they
/// must change it on first login (the implementation should set a flag
/// requiring a password change, OR the admin communicates the temporary
/// password out-of-band).
///
/// AUTHORIZATION:
///   Admin only. Managers cannot reset passwords — too sensitive.
///
/// TWO-STEP IDENTITY OPERATION:
///   This command only calls <c>IUserAccountService.ResetPasswordAsync</c>.
///   It does NOT touch the domain User (password is not a domain concern).
///   SaveChangesAsync is still called to commit the Identity change in
///   the same transaction (assuming Infrastructure shares the DbContext).
///
/// SELF-SERVICE:
///   Admin can reset their own password via this command (no self-block).
///   Users changing their OWN password (with knowledge of the old one)
///   go through a separate flow not modeled here.
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed record ResetUserPasswordCommand(Guid UserId, string NewPassword);
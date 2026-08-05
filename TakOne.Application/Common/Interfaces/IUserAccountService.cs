using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Abstraction over ASP.NET Identity's <c>UserManager</c> for the operations
/// the Application layer needs but the Domain <c>User</c> aggregate cannot
/// model (because the Domain is framework-free).
///
/// WHY THIS EXISTS:
///   The Domain User only knows (WorkerId, FullName, GroupName?, Gender, IsActive).
///   Email, password hash, security stamp, and ASP.NET Identity roles all
///   live on <c>ApplicationUser</c> in the Infrastructure layer. To create
///   a login-capable user from the Application layer, we need to:
///     1. Create the Domain User via <c>IUserRepository.AddAsync</c>
///        (generates a Guid Id).
///     2. Create the ApplicationUser with that SAME Guid Id, set its
///        email + password, copy over the Gender (denormalized for the
///        admin user-management page), and assign a role.
///   This interface exposes step 2 as an Application-layer concern so
///   command handlers stay framework-agnostic.
///
/// IMPLEMENTATION:
///   Implemented in the Infrastructure layer (step 7) using
///   <c>UserManager&lt;ApplicationUser&gt;</c>. The implementation must
///   be transactional with the Domain User creation — either by sharing
///   the same EF Core DbContext (recommended) or by using an explicit
///   transaction scope.
///
/// FAILURE SEMANTICS:
///   All methods return <see cref="Result"/>. On Identity failure (weak
///   password, duplicate email, role doesn't exist), they return
///   <c>Result.Failure(identityErrorMessage)</c>. The handler is
///   responsible for surfacing the message to the caller.
/// </summary>
public interface IUserAccountService
{
    /// <summary>
    /// Creates the ASP.NET Identity account for an already-persisted Domain
    /// User. The ApplicationUser.Id is set to <paramref name="userId"/> so
    /// the two share a primary key (one-to-one).
    ///
    /// Steps performed by the implementation:
    ///   1. Construct ApplicationUser with Id = userId, UserName = workerId,
    ///      Email = email, Gender = gender (denormalized copy — see remark
    ///      on <c>ApplicationUser.Gender</c>).
    ///   2. Set the password via UserManager.CreateAsync.
    ///   3. Assign the user to the given role via UserManager.AddToRoleAsync.
    ///   4. Ensure the email is confirmed (admin-created accounts skip the
    ///      email confirmation flow).
    /// </summary>
    /// <param name="gender">
    /// The user's gender. Copied onto the ApplicationUser so the admin
    /// user-management page can display it without joining to the Domain
    /// Users table. The Domain User remains the source of truth —
    /// <c>ChangeGender</c> updates the Domain User; a future
    /// <c>UpdateIdentityAccountAsync</c> call would sync the copy.
    /// </param>
    Task<Result> CreateIdentityAccountAsync(
        Guid userId,
        string workerId,
        string email,
        string initialPassword,
        string role,
        Gender gender,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the user's password. Admin-only operation — users change
    /// their own password via a separate flow (not this interface).
    /// </summary>
    Task<Result> ResetPasswordAsync(
        Guid userId,
        string newPassword,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a self-service password-reset token for the user identified
    /// by <paramref name="email"/>. This is the entry point of the
    /// "Forgot Password" flow.
    ///
    /// ENUMERATION DEFENSE:
    ///   Returns <c>null</c> both when no user has the given email AND when
    ///   the user is found but deactivated. The caller MUST treat both
    ///   cases identically — surfacing "user not found" vs "user found" lets
    ///   an attacker enumerate valid email addresses by submitting the form
    ///   repeatedly and observing differences in the response. The Forgot
    ///   Password page always shows the same generic "if your email is
    ///   registered, a reset link has been sent" message regardless of the
    ///   return value.
    ///
    /// WHAT THE CALLER DOES WITH THE TOKEN:
    ///   The token is opaque (a base64-encoded signed string from Identity's
    ///   token provider). The caller embeds it in a reset URL like
    ///   <c>/Account/ResetPassword?email=...&amp;token=...</c> and either:
    ///     - Emails it to the user (production), or
    ///     - Logs it (dev — when SMTP isn't configured).
    ///   The token is single-use and expires after Identity's
    ///   <c>TokenLifespan</c> (default 24h).
    /// </summary>
    /// <returns>
    /// The reset token, or <c>null</c> if no ACTIVE user has the given email.
    /// </returns>
    Task<string?> GeneratePasswordResetTokenAsync(
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the user's password using a token obtained from
    /// <see cref="GeneratePasswordResetTokenAsync"/>. This is the
    /// self-service flow (user clicks link in email and enters a new
    /// password). Admin-driven resets use
    /// <see cref="ResetPasswordAsync(Guid, string, CancellationToken)"/>
    /// which bypasses the token flow.
    ///
    /// FAILURE MODES:
    ///   - Email not registered → <c>Result.Failure</c> with a generic
    ///     message (do NOT distinguish "user not found" from "invalid token"
    ///     in user-facing copy — both mean the link is invalid).
    ///   - Token invalid/expired/already-used → <c>Result.Failure</c> with
    ///     Identity's error description.
    ///   - Password fails Identity's complexity rules →
    ///     <c>Result.Failure</c> with Identity's error description.
    ///   - User deactivated between token-issue and reset →
    ///     <c>Result.Failure</c> with a generic "link is invalid" message.
    /// </summary>
    Task<Result> ResetPasswordFromTokenAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns an ASP.NET Identity role to the user. Idempotent — if the
    /// user already has the role, returns success without re-adding.
    /// </summary>
    Task<Result> AssignRoleAsync(
        Guid userId,
        string role,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an ASP.NET Identity role from the user. Idempotent — if the
    /// user doesn't have the role, returns success.
    /// </summary>
    Task<Result> RemoveFromRoleAsync(
        Guid userId,
        string role,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the list of ASP.NET Identity roles currently assigned to
    /// the user. Used by query handlers to populate the UserDto.
    /// </summary>
    Task<IReadOnlyList<string>> GetRolesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Self-service password change. Verifies the user's current password,
    /// then sets the new password. Used by the
    /// <c>/Account/ChangePassword</c> page — including the
    /// forced-first-login flow triggered by
    /// <see cref="TakOne.Infrastructure.Identity.ApplicationUser.MustChangePassword"/>.
    ///
    /// WHY THIS LIVES ON IUserAccountService (not just on UserManager):
    ///   The WebUI layer's Razor page could in principle call
    ///   <c>UserManager.ChangePasswordAsync</c> directly — the method
    ///   takes (user, currentPassword, newPassword). But routing through
    ///   the Application-layer abstraction gives us:
    ///     1. A single, well-documented failure surface (returns
    ///        <see cref="Result"/> with a flattened error string, same as
    ///        every other Identity operation in this interface).
    ///     2. A place to enforce any future domain-side invariants (e.g.
    ///        "you cannot reuse any of your last 5 passwords" — currently
    ///        not implemented, but easy to add here without touching the
    ///        Razor page).
    ///     3. Testability — the ChangePassword page can be unit-tested
    ///        against a fake <c>IUserAccountService</c> without standing
    ///        up a real UserManager + DbContext.
    ///
    /// FAILURE MODES:
    ///   - User not found → <c>Result.Failure</c> with a generic message.
    ///   - Current password incorrect → <c>Result.Failure</c> with
    ///     Identity's <c>PasswordMismatch</c> error description
    ///     (localized by <c>TakOneIdentityErrorDescriber</c>).
    ///   - New password fails Identity's complexity rules →
    ///     <c>Result.Failure</c> with the relevant Identity error
    ///     description.
    ///   - New password is identical to the current password → handled
    ///     by the caller (the Razor page validates this client-side via
    ///     <c>[Compare]</c>); defense-in-depth, the page also rejects
    ///     server-side if the strings are equal before calling this
    ///     method.
    /// </summary>
    /// <param name="userId">
    /// The user's Id (the shared PK on both <c>Domain.User</c> and
    /// <c>ApplicationUser</c>).
    /// </param>
    /// <param name="currentPassword">
    /// The user's current password, as typed into the "current password"
    /// field on the ChangePassword form. Verified by
    /// <c>UserManager.ChangePasswordAsync</c> — we never read the stored
    /// hash directly.
    /// </param>
    /// <param name="newPassword">
    /// The new password, as typed into the "new password" field. Must
    /// satisfy Identity's complexity rules.
    /// </param>
    Task<Result> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);
}
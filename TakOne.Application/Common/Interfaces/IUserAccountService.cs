using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Abstraction over ASP.NET Identity's <c>UserManager</c> for the operations
/// the Application layer needs but the Domain <c>User</c> aggregate cannot
/// model (because the Domain is framework-free).
///
/// WHY THIS EXISTS:
///   The Domain User only knows (WorkerId, FullName, GroupName?, IsActive).
///   Email, password hash, security stamp, and ASP.NET Identity roles all
///   live on <c>ApplicationUser</c> in the Infrastructure layer. To create
///   a login-capable user from the Application layer, we need to:
///     1. Create the Domain User via <c>IUserRepository.AddAsync</c>
///        (generates a Guid Id).
///     2. Create the ApplicationUser with that SAME Guid Id, set its
///        email + password, and assign a role.
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
    ///      Email = email.
    ///   2. Set the password via UserManager.CreateAsync.
    ///   3. Assign the user to the given role via UserManager.AddToRoleAsync.
    ///   4. Ensure the email is confirmed (admin-created accounts skip the
    ///      email confirmation flow).
    /// </summary>
    Task<Result> CreateIdentityAccountAsync
        (
        Guid userId,
        string workerId,
        string email,
        string initialPassword,
        string role,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Resets the user's password. Admin-only operation — users change
    /// their own password via a separate flow (not this interface).
    /// </summary>
    Task<Result> ResetPasswordAsync
        (
        Guid userId,
        string newPassword,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Assigns an ASP.NET Identity role to the user. Idempotent — if the
    /// user already has the role, returns success without re-adding.
    /// </summary>
    Task<Result> AssignRoleAsync
        (
        Guid userId,
        string role,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Removes an ASP.NET Identity role from the user. Idempotent — if the
    /// user doesn't have the role, returns success.
    /// </summary>
    Task<Result> RemoveFromRoleAsync
        (
        Guid userId,
        string role,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Returns the list of ASP.NET Identity roles currently assigned to
    /// the user. Used by query handlers to populate the UserDto.
    /// </summary>
    Task<IReadOnlyList<string>> GetRolesAsync
        (
        Guid userId,
        CancellationToken cancellationToken = default
        );
}
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.ResetUserPassword;

/// <summary>
/// Admin resets a user's password (Identity-only, no domain change).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class ResetUserPasswordCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        ResetUserPasswordCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUserAccountService userAccountService,
        IUnitOfWork unitOfWork,
        ILogger<ResetUserPasswordCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ResetUserPassword: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 0a. Role check (defense-in-depth).
        //
        // The command is decorated [RequireRoles(Roles.Admin)] and the
        // AuthorizationMiddleware enforces it, but resetting ANY user's
        // password is a full ACCOUNT-TAKEOVER primitive — the single most
        // sensitive operation in the system. If this handler is ever
        // reached through a path that bypasses the middleware (a future
        // HTTP endpoint, a background job, a tampered circuit), an
        // explicit in-handler check is the last line of defense.
        // Mirrors the CreateStaffCommandHandler pattern.
        // ------------------------------------------------------------------
        if (!currentUser.IsInRole(Roles.Admin))
        {
            logger.LogWarning
                ("ResetUserPassword: caller {ActorId} is not Admin. Only administrators may reset passwords. Rejected.",
                currentUser.UserId);

            return Result.Failure("Only administrators may reset user passwords.");
        }

        // ------------------------------------------------------------------
        // 1. Load the domain User. We need it to (a) confirm the target
        //    exists and (b) log a meaningful audit entry (worker ID, etc.).
        //    The password itself lives on ApplicationUser — the domain
        //    User is not modified.
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("ResetUserPassword: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to IUserAccountService. The Infrastructure layer uses
        //    UserManager.GeneratePasswordResetTokenAsync + ResetPasswordAsync
        //    (or RemovePasswordAsync + AddPasswordAsync) to set the new
        //    password. If the new password fails Identity's complexity
        //    rules, the result will carry the Identity error message.
        // ------------------------------------------------------------------
        var result = await userAccountService.ResetPasswordAsync(user.Id, command.NewPassword, cancellationToken);

        if (result.IsFailure)
        {
            logger.LogWarning
                ("ResetUserPassword: Identity rejected password reset for user {UserId} (worker ID '{WorkerId}'). Reason: {Reason}. Requested by user {ActorId}.",
                user.Id, user.WorkerId, result.Error, currentUser.UserId);

            return result;
        }

        // ------------------------------------------------------------------
        // 3. SaveChangesAsync commits the Identity changes. The domain User
        //    is unchanged, so EF Core won't emit any UPDATE for it — only
        //    the ApplicationUser row (and possibly SecurityStamp) updates.
        // ------------------------------------------------------------------
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("ResetUserPassword: password reset for user {UserId} (worker ID '{WorkerId}') by user {ActorId}.",
            user.Id, user.WorkerId, currentUser.UserId);

        return Result.Success();
    }
}
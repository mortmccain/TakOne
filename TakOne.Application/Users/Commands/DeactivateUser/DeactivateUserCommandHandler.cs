using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.DeactivateUser;

/// <summary>
/// Soft-deletes a user (sets IsActive = false).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class DeactivateUserCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        DeactivateUserCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        ILogger<DeactivateUserCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("DeactivateUser: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Reject self-deactivation. An admin who deactivates themselves
        //    would lock themselves out with no recovery path short of DB
        //    access. This is a friendly guard, not a security control —
        //    the role check above already ensures only admins/managers
        //    reach this point.
        // ------------------------------------------------------------------
        if (command.UserId == currentUser.UserId)
        {
            logger.LogWarning
                ("DeactivateUser: user {ActorId} attempted to deactivate themselves. Rejected.",
                currentUser.UserId);

            return Result.Failure("You cannot deactivate your own account.");
        }

        // ------------------------------------------------------------------
        // 2. Load the user.
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("DeactivateUser: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 3. Delegate to the aggregate. Deactivate is idempotent.
        // ------------------------------------------------------------------
        user.Deactivate();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("DeactivateUser: user {UserId} (worker ID '{WorkerId}') deactivated by user {ActorId}.",
            user.Id, user.WorkerId, currentUser.UserId);

        return Result.Success();
    }
}
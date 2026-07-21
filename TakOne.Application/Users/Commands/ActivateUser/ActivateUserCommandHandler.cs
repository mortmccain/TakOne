using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.ActivateUser;

/// <summary>
/// Reactivates a previously deactivated user (domain-only).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class ActivateUserCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        ActivateUserCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        ILogger<ActivateUserCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ActivateUser: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the user.
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("ActivateUser: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Defensive guard: don't let a user activate themselves if they
        //    are the current user (they're already active if they're
        //    making the call). This isn't a security issue but it would
        //    produce a misleading audit log. We allow it but log a warning.
        // ------------------------------------------------------------------
        if (user.Id == currentUser.UserId)
        {
            logger.LogWarning
                ("ActivateUser: user {ActorId} attempted to activate themselves. Allowing (no-op).",
                currentUser.UserId);

            return Result.Failure("You cannot activate yourself. You are already active.");
        }

        // ------------------------------------------------------------------
        // 3. Delegate to the aggregate. Activate is idempotent.
        // ------------------------------------------------------------------
        user.Activate();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("ActivateUser: user {UserId} (worker ID '{WorkerId}') activated by user {ActorId}.",
            user.Id, user.WorkerId, currentUser.UserId);

        return Result.Success();
    }
}
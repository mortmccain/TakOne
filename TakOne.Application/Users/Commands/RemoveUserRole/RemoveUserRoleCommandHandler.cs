using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.RemoveUserFromGroup;

/// <summary>
/// Removes a user from their customer group (domain-only).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class RemoveUserFromGroupCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        RemoveUserFromGroupCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        ILogger<RemoveUserFromGroupCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("RemoveUserFromGroup: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the user.
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("RemoveUserFromGroup: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. RemoveFromGroup is idempotent —
        //    sets GroupId to null unconditionally. We persist either way;
        //    in the no-op case, EF Core simply won't detect any changes
        //    and SaveChangesAsync is a null-op round-trip.
        // ------------------------------------------------------------------
        var previousGroupId = user.GroupId;
        user.RemoveFromGroup();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("RemoveUserFromGroup: user {UserId} (worker ID '{WorkerId}') removed from group {PreviousGroupId} by user {ActorId}.",
            user.Id, user.WorkerId, previousGroupId, currentUser.UserId);

        return Result.Success();
    }
}
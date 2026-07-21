using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.AssignUserToGroup;

/// <summary>
/// Assigns a user to a customer group (domain-only).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class AssignUserToGroupCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        AssignUserToGroupCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        ILogger<AssignUserToGroupCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("AssignUserToGroup: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the user.
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("AssignUserToGroup: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. AssignToGroup validates the group
        //    name (non-empty, ≤ 100 chars). DomainException is caught by
        //    middleware.
        // ------------------------------------------------------------------
        user.AssignToGroup(command.GroupName);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("AssignUserToGroup: user {UserId} (worker ID '{WorkerId}') assigned to group '{Group}' by user {ActorId}.",
            user.Id, user.WorkerId, user.GroupName, currentUser.UserId);

        return Result.Success();
    }
}
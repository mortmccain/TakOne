using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.RemoveUserRole;

/// <summary>
/// Removes an ASP.NET Identity role from a user.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class RemoveUserRoleCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        RemoveUserRoleCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUserAccountService userAccountService,
        IUnitOfWork unitOfWork,
        ILogger<RemoveUserRoleCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("RemoveUserRole: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Self-removal guard. An admin removing the Admin role from
        //    themselves would lock themselves out — reject upfront.
        //    (Transferring admin privileges requires assigning Admin to
        //    another user FIRST, then removing it from yourself — but
        //    that's a workflow concern, not a code concern.)
        // ------------------------------------------------------------------
        if (command.UserId == currentUser.UserId && command.Role == Roles.Admin)
        {
            logger.LogWarning
                ("RemoveUserRole: user {ActorId} attempted to remove their own Admin role. Rejected.",
                currentUser.UserId);

            return Result.Failure
                ("You cannot remove your own Admin role. Assign Admin to another user first.");
        }

        // ------------------------------------------------------------------
        // 2. Load the domain User (for audit logging + existence check).
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("RemoveUserRole: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 3. Delegate to IUserAccountService. Idempotent — returns success
        //    even if the user didn't have the role.
        // ------------------------------------------------------------------
        var result = await userAccountService.RemoveFromRoleAsync(user.Id, command.Role, cancellationToken);

        if (result.IsFailure)
        {
            logger.LogWarning
                ("RemoveUserRole: Identity rejected role removal '{Role}' for user {UserId} (worker ID '{WorkerId}'). Reason: {Reason}. Requested by user {ActorId}.",
                command.Role, user.Id, user.WorkerId, result.Error, currentUser.UserId);

            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("RemoveUserRole: role '{Role}' removed from user {UserId} (worker ID '{WorkerId}') by user {ActorId}.",
            command.Role, user.Id, user.WorkerId, currentUser.UserId);

        return Result.Success();
    }
}
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
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
        // 2. Manager scope enforcement (Phase 6.3, updated 6.4).
        //
        // A Manager (in Manager role but NOT Admin) may only deactivate
        // users in the Employee OR Customer role — they cannot deactivate
        // other managers, admins, or read-onlys. The AdminUsers UI hides
        // the deactivate button on rows the manager can't touch, so this
        // server-side check is defense-in-depth against a tampered request.
        //
        // We look up the target user's roles via the batch-friendly
        // GetRolesByUserIdsAsync (single-row variant — passing one Id).
        // ------------------------------------------------------------------
        var isCallerAdmin = currentUser.IsInRole(Roles.Admin);
        var isCallerManager = currentUser.IsInRole(Roles.Manager);

        if (isCallerManager && !isCallerAdmin)
        {
            var targetRoles = await userRepository.GetRolesByUserIdsAsync(
                new[] { command.UserId }, cancellationToken);

            targetRoles.TryGetValue(command.UserId, out var roles);
            var rolesList = roles ?? new List<string>();

            var canAct = rolesList.Contains(Roles.Employee)
                      || rolesList.Contains(Roles.Customer);

            // Also block if the target is themselves (already covered above)
            // OR if the target is not (only) an Employee/Customer. Note: a
            // user CAN be in multiple roles — if Employee or Customer is one
            // of them, we allow the action; the rule is "managers may act on
            // employees or customers", and a user who has Employee plus other
            // roles still counts.
            if (!canAct)
            {
                logger.LogWarning
                    ("DeactivateUser: Manager {ActorId} attempted to deactivate user {TargetId} who is neither Employee nor Customer. Rejected.",
                    currentUser.UserId, command.UserId);

                return Result.Failure
                    ("Managers may only deactivate Employee or Customer accounts. Deactivating other managers, administrators, or read-only users requires Administrator access.");
            }
        }

        // ------------------------------------------------------------------
        // 3. Load the user.
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
        // 4. Delegate to the aggregate. Deactivate is idempotent.
        // ------------------------------------------------------------------
        user.Deactivate();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("DeactivateUser: user {UserId} (worker ID '{WorkerId}') deactivated by user {ActorId}.",
            user.Id, user.WorkerId, currentUser.UserId);

        return Result.Success();
    }
}
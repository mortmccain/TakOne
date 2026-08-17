using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Customers.Commands.DeactivateCustomerGroup;

public sealed class DeactivateCustomerGroupCommandHandler
{
    public static async Task<Result> HandleAsync(
        DeactivateCustomerGroupCommand command,
        ICurrentUserService currentUser,
        ICustomerGroupRepository customerGroupRepository,
        IUnitOfWork unitOfWork,
        ILogger<DeactivateCustomerGroupCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("DeactivateCustomerGroup: unauthenticated call rejected.");
            return Result.Failure("Authentication required.");
        }

        var group = await customerGroupRepository.GetByIdAsync(command.GroupId, cancellationToken);
        if (group is null)
        {
            logger.LogWarning(
                "DeactivateCustomerGroup: group {GroupId} not found. Requested by user {UserId}.",
                command.GroupId, currentUser.UserId);
            return Result.Failure($"Customer group '{command.GroupId}' was not found.");
        }

        // ------------------------------------------------------------------
        // Load the count of active users in this group — for audit log.
        // The UI should have warned the admin BEFORE issuing this command
        // if the count is non-zero (the handler still succeeds, but the
        // log entry records the impact).
        // ------------------------------------------------------------------
        var activeUserCount = await customerGroupRepository.GetActiveUserCountAsync(
            command.GroupId, cancellationToken);

        var wasActive = group.IsActive;
        group.Deactivate();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "DeactivateCustomerGroup: group {GroupId} ('{Name}') deactivated by user {UserId}. " +
            "Was previously active: {WasActive}. Active users affected: {ActiveUserCount}.",
            group.Id, group.Name, currentUser.UserId, wasActive, activeUserCount);

        return Result.Success();
    }
}
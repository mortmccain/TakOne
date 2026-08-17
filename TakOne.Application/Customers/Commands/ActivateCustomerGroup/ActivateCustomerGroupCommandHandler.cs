using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Customers.Commands.ActivateCustomerGroup;

public sealed class ActivateCustomerGroupCommandHandler
{
    public static async Task<Result> HandleAsync(
        ActivateCustomerGroupCommand command,
        ICurrentUserService currentUser,
        ICustomerGroupRepository customerGroupRepository,
        IUnitOfWork unitOfWork,
        ILogger<ActivateCustomerGroupCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ActivateCustomerGroup: unauthenticated call rejected.");
            return Result.Failure("Authentication required.");
        }

        var group = await customerGroupRepository.GetByIdAsync(command.GroupId, cancellationToken);
        if (group is null)
        {
            logger.LogWarning(
                "ActivateCustomerGroup: group {GroupId} not found. Requested by user {UserId}.",
                command.GroupId, currentUser.UserId);
            return Result.Failure($"Customer group '{command.GroupId}' was not found.");
        }

        var wasActive = group.IsActive;
        group.Activate();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "ActivateCustomerGroup: group {GroupId} activated by user {UserId}. Was previously active: {WasActive}.",
            group.Id, currentUser.UserId, wasActive);

        return Result.Success();
    }
}
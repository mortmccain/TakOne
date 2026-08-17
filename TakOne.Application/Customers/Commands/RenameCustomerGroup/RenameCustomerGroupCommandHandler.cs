using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Customers.Commands.RenameCustomerGroup;

public sealed class RenameCustomerGroupCommandHandler
{
    public static async Task<Result> HandleAsync(
        RenameCustomerGroupCommand command,
        ICurrentUserService currentUser,
        ICustomerGroupRepository customerGroupRepository,
        IUnitOfWork unitOfWork,
        ILogger<RenameCustomerGroupCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("RenameCustomerGroup: unauthenticated call rejected.");
            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the group (tracked — we'll mutate it).
        // ------------------------------------------------------------------
        var group = await customerGroupRepository.GetByIdAsync(command.GroupId, cancellationToken);
        if (group is null)
        {
            logger.LogWarning(
                "RenameCustomerGroup: group {GroupId} not found. Requested by user {UserId}.",
                command.GroupId, currentUser.UserId);
            return Result.Failure($"Customer group '{command.GroupId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Name uniqueness (excluding this group — it's OK for the group
        //    to keep its current name if the admin didn't change it).
        // ------------------------------------------------------------------
        var nameExistsForAnother = await customerGroupRepository.NameExistsAsync(
            command.NewName, excludeId: command.GroupId, cancellationToken);

        if (nameExistsForAnother)
        {
            logger.LogWarning(
                "RenameCustomerGroup: name '{Name}' already exists for another group. User {UserId} rejected.",
                command.NewName, currentUser.UserId);
            return Result.Failure(
                $"Another customer group already uses the name '{command.NewName}'. Choose a different name.");
        }

        // ------------------------------------------------------------------
        // 3. Delegate to the aggregate. Rename enforces the domain
        //    invariant (Name 1..100 chars). DomainException is caught by
        //    middleware.
        // ------------------------------------------------------------------
        var previousName = group.Name;
        group.Rename(command.NewName);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RenameCustomerGroup: group {GroupId} renamed from '{PreviousName}' to '{NewName}' by user {UserId}.",
            group.Id, previousName, group.Name, currentUser.UserId);

        return Result.Success();
    }
}
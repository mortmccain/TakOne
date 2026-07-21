using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.UpdateUserFullName;

/// <summary>
/// Updates a user's full name (domain-only).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class UpdateUserFullNameCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        UpdateUserFullNameCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        ILogger<UpdateUserFullNameCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("UpdateUserFullName: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the user. EF Core tracks changes, so calling ChangeFullName
        //    on the loaded entity is enough — no explicit Update needed at
        //    SaveChanges time.
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("UpdateUserFullName: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to the aggregate. ChangeFullName enforces non-empty +
        //    length ≤ 200. DomainException is caught by middleware.
        // ------------------------------------------------------------------
        user.ChangeFullName(command.NewFullName);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("UpdateUserFullName: user {UserId} renamed to '{NewName}' by user {ActorId}.",
            user.Id, user.FullName, currentUser.UserId);

        return Result.Success();
    }
}
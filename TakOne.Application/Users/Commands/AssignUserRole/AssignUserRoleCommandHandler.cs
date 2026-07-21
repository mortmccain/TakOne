using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.AssignUserRole;

/// <summary>
/// Assigns an ASP.NET Identity role to a user.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class AssignUserRoleCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        AssignUserRoleCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUserAccountService userAccountService,
        IUnitOfWork unitOfWork,
        ILogger<AssignUserRoleCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("AssignUserRole: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the domain User (for audit logging + existence check).
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("AssignUserRole: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Delegate to IUserAccountService. The implementation calls
        //    UserManager.AddToRoleAsync. If the role doesn't exist in the
        //    IdentityRole table, the result will carry the Identity error.
        //    The validator already checked that the role name is one of
        //    the known Roles constants, but a misconfigured seed could
        //    still cause this to fail.
        // ------------------------------------------------------------------
        var result = await userAccountService.AssignRoleAsync(user.Id, command.Role, cancellationToken);

        if (result.IsFailure)
        {
            logger.LogWarning
                ("AssignUserRole: Identity rejected role assignment '{Role}' for user {UserId} (worker ID '{WorkerId}'). Reason: {Reason}. Requested by user {ActorId}.",
                command.Role, user.Id, user.WorkerId, result.Error, currentUser.UserId);

            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("AssignUserRole: role '{Role}' assigned to user {UserId} (worker ID '{WorkerId}') by user {ActorId}.",
            command.Role, user.Id, user.WorkerId, currentUser.UserId);

        return Result.Success();
    }
}
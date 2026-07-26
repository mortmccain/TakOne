using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.CreateStaff;

/// <summary>
/// Creates a new STAFF user (Domain User + ApplicationUser + staff role).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class CreateStaffCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        CreateStaffCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUserAccountService userAccountService,
        IUnitOfWork unitOfWork,
        ILogger<CreateStaffCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateStaff: unauthenticated call rejected.");

            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. WorkerId uniqueness. Same rationale as CreateCustomer.
        // ------------------------------------------------------------------
        var workerIdExists = await userRepository.WorkerIdExistsAsync(command.WorkerId, cancellationToken);

        if (workerIdExists)
        {
            logger.LogWarning
                ("CreateStaff: worker ID '{WorkerId}' already exists. Requested by user {UserId}.",
                command.WorkerId, currentUser.UserId);

            return Result<Guid>.Failure
                ($"A user with worker ID '{command.WorkerId}' already exists.");
        }

        // 2. Create the Domain User via the staff factory. Staff users have
        //    no GroupName — the factory accepts only (workerId, fullName, gender).
        // ------------------------------------------------------------------
        var user = User.CreateStaff(command.WorkerId, command.FullName, command.Gender);

        // ------------------------------------------------------------------
        // 3. Track the Domain User (no SaveChanges yet — see CreateCustomer
        //    handler for the rationale).
        // ------------------------------------------------------------------
        await userRepository.AddAsync(user, cancellationToken);

        // ------------------------------------------------------------------
        // 4. Create the ApplicationUser with the same Guid Id, set email +
        //    password, copy the Gender (denormalized), and assign the
        //    requested staff role. The role has already been validated by
        //    the validator (must be one of the AllowedStaffRoles), so we
        //    trust it here.
        // ------------------------------------------------------------------
        var accountResult = await userAccountService.CreateIdentityAccountAsync
            (
            user.Id,
            user.WorkerId,
            command.Email,
            command.InitialPassword,
            command.Role,
            user.Gender,
            cancellationToken
            );

        if (accountResult.IsFailure)
        {
            logger.LogWarning
                ("CreateStaff: Identity account creation failed for worker ID '{WorkerId}' (role '{Role}'). Reason: {Reason}. Requested by user {UserId}.",
                command.WorkerId, command.Role, accountResult.Error, currentUser.UserId);

            return Result<Guid>.Failure(accountResult.Error);
        }

        // ------------------------------------------------------------------
        // 5. SaveChangesAsync commits both the Domain User and the Identity
        //    account in one transaction (assuming Infrastructure shares the
        //    DbContext — see CreateCustomer handler comment).
        // ------------------------------------------------------------------
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("CreateStaff: staff user {UserId} (worker ID '{WorkerId}', role '{Role}') created by user {ActorId}.",
            user.Id, user.WorkerId, command.Role, currentUser.UserId);

        return Result<Guid>.Success(user.Id);
    }
}
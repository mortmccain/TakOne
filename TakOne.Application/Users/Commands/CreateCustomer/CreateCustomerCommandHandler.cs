using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.CreateCustomer;

/// <summary>
/// Creates a new CUSTOMER user (Domain User + ApplicationUser + Customer role).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class CreateCustomerCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        CreateCustomerCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUserAccountService userAccountService,
        IUnitOfWork unitOfWork,
        ILogger<CreateCustomerCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateCustomer: unauthenticated call rejected.");

            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. WorkerId uniqueness. WorkerId is the login identifier — it must
        //    be globally unique. The handler does the friendly pre-check;
        //    the DB unique index on ApplicationUser.UserName is the hard
        //    guarantee against concurrent races.
        // ------------------------------------------------------------------
        var workerIdExists = await userRepository.WorkerIdExistsAsync(command.WorkerId, cancellationToken);

        if (workerIdExists)
        {
            logger.LogWarning
                ("CreateCustomer: worker ID '{WorkerId}' already exists. Requested by user {UserId}.",
                command.WorkerId, currentUser.UserId);

            return Result<Guid>.Failure
                ($"A user with worker ID '{command.WorkerId}' already exists.");
        }

        // ------------------------------------------------------------------
        // 2. Create the Domain User via the customer factory. This enforces
        //    the domain invariants (WorkerId/FullName/GroupId non-empty,
        //    length caps, Gender is a defined enum value). DomainException
        //    is caught by middleware.
        // ------------------------------------------------------------------
        var user = User.CreateCustomer(command.WorkerId, command.FullName, command.GroupId, command.Gender);

        // ------------------------------------------------------------------
        // 3. Persist the Domain User. This generates the user's Guid Id,
        //    which we then pass to IUserAccountService so the ApplicationUser
        //    shares the same primary key.
        //
        //    Note: we call AddAsync (which tracks the entity) but do NOT
        //    call SaveChangesAsync yet. If the Identity account creation
        //    fails, we want to be able to bail out without persisting the
        //    orphaned Domain User. SaveChangesAsync runs at the end, after
        //    both phases have succeeded.
        // ------------------------------------------------------------------
        await userRepository.AddAsync(user, cancellationToken);

        // ------------------------------------------------------------------
        // 4. Create the ApplicationUser (ASP.NET Identity account) with the
        //    same Guid Id. This sets email + password, copies the Gender
        //    (denormalized), and assigns the Customer role. If it fails
        //    (weak password, duplicate email, role not seeded), we return
        //    the failure and the in-memory Domain User is discarded (no
        //    SaveChangesAsync yet).
        // ------------------------------------------------------------------
        var accountResult = await userAccountService.CreateIdentityAccountAsync
            (
            user.Id,
            user.WorkerId,
            command.Email,
            command.InitialPassword,
            Roles.Customer,
            user.Gender,
            cancellationToken
            );

        if (accountResult.IsFailure)
        {
            logger.LogWarning
                ("CreateCustomer: Identity account creation failed for worker ID '{WorkerId}'. Reason: {Reason}. Requested by user {UserId}.",
                command.WorkerId, accountResult.Error, currentUser.UserId);

            // v6.2 — CRITICAL: detach the Domain User we tracked at step 3.
            //
            // Without this, the handler returns Result.Failure (a normal
            // return, NOT an exception). Wolverine's DbTransactionMiddleware
            // sees a normal return and proceeds to call SaveChangesAsync +
            // CommitAsync on the scoped DbContext — which would persist the
            // tracked Domain User even though Identity account creation
            // failed. The result: an orphan Domain User with no
            // corresponding ApplicationUser (no login possible, but the
            // user shows up in admin lists). Horrible bug.
            //
            // ClearChangeTracker calls DbContext.ChangeTracker.Clear(),
            // detaching ALL tracked entities. In this handler, the only
            // tracked entity is the new Domain User we AddedAsync'd above,
            // so the clear is safe — no other in-flight work is lost.
            //
            // Alternative considered: throw an exception to make Wolverine
            // roll back. Rejected because it loses the structured
            // Result<Guid> pattern the rest of the handler uses and forces
            // the Razor page to wrap every call in try/catch.
            unitOfWork.ClearChangeTracker();

            return Result<Guid>.Failure(accountResult.Error);
        }

        // ------------------------------------------------------------------
        // 5. SaveChangesAsync commits both the Domain User INSERT and any
        //    Identity changes (ApplicationUser + role assignment) in a
        //    single transaction — IF the Infrastructure layer wires
        //    IUserAccountService to share the same DbContext. (If it uses
        //    a separate UserManager with its own store, the Infrastructure
        //    layer is responsible for wrapping both in an explicit
        //    transaction scope.)
        // ------------------------------------------------------------------
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("CreateCustomer: customer {UserId} (worker ID '{WorkerId}', group {GroupId}) created by user {ActorId}.",
            user.Id, user.WorkerId, user.GroupId, currentUser.UserId);

        return Result<Guid>.Success(user.Id);
    }
}
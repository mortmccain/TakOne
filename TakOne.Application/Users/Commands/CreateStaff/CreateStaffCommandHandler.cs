using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
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
        // 0a. Manager scope enforcement.
        //
        // A Manager (i.e. a caller who is in the Manager role but NOT in the
        // Admin role) may ONLY create Employee staff accounts. Any other
        // staff role (Manager, ReadOnly, Admin) is rejected. The UI on the
        // Create User page restricts the role dropdown to just Employee
        // when a Manager is signed in, so this server-side check is
        // defense-in-depth against a tampered request.
        //
        // Note: we use IsInRole(Roles.Admin) here, which works in the
        // Wolverine handler context (unlike Blazor interactive mode, the
        // HttpContext is available here via ICurrentUserService).
        // ------------------------------------------------------------------
        var isCallerAdmin = currentUser.IsInRole(Roles.Admin);
        var isCallerManager = currentUser.IsInRole(Roles.Manager);

        if (isCallerManager && !isCallerAdmin && command.Role != Roles.Employee)
        {
            logger.LogWarning
                ("CreateStaff: Manager {ActorId} attempted to create staff with role '{Role}' (only Employee allowed for Managers). Rejected.",
                currentUser.UserId, command.Role);

            return Result<Guid>.Failure
                ("Managers may only create Employee staff accounts. Creating Manager, Read-only, or Admin accounts requires Administrator access.");
        }

        // Defense-in-depth: the [RequireRoles(Admin, Manager)] attribute on
        // the command already rejects callers who are neither Admin nor
        // Manager. If somehow a non-staff caller reaches here (e.g. role
        // attribute was bypassed), reject explicitly.
        if (!isCallerAdmin && !isCallerManager)
        {
            logger.LogWarning
                ("CreateStaff: caller {ActorId} is neither Admin nor Manager. Rejected.",
                currentUser.UserId);

            return Result<Guid>.Failure("Only administrators and managers may create staff accounts.");
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
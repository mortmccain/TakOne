using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.ActivateUser;

/// <summary>
/// Reactivates a previously deactivated user.
///
/// <b>DOMAIN + IDENTITY SYNC:</b>
/// <para>
/// Same bug-fix pattern as <c>DeactivateUserCommandHandler</c> — this
/// handler updates BOTH the Domain <c>User.IsActive</c> flag (via the
/// aggregate's <c>Activate()</c> method) AND the Identity
/// <c>ApplicationUser.IsActive</c> flag (via
/// <c>IUserAccountService.SetUserActiveStatusAsync</c>). Without the
/// Identity-side update, the <c>Login.razor</c> pre-check that reads
/// <c>appUser.IsActive</c> would still see <c>false</c> (from when the
/// user was deactivated) and reject the login — making reactivation a
/// no-op.
/// </para>
/// <para>
/// The previous version of this handler was documented as "domain-only"
/// (a known limitation). This version closes that hole.
/// </para>
/// </summary>
public sealed class ActivateUserCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        ActivateUserCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUserAccountService userAccountService,
        IUnitOfWork unitOfWork,
        ILogger<ActivateUserCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("ActivateUser: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Manager scope enforcement (Phase 6.3, updated 6.4).
        //
        // Same rule as DeactivateUserCommandHandler: a Manager (in Manager
        // role but NOT Admin) may only activate users in the Employee OR
        // Customer role. The AdminUsers UI hides the activate button on rows
        // the manager can't touch, so this server-side check is defense-in-depth.
        //
        // We do the role check BEFORE loading the Domain User because the
        // role lookup uses the Identity tables (joined by user Id) — we
        // don't need the Domain User for that, just the Id.
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

            if (!canAct)
            {
                logger.LogWarning
                    ("ActivateUser: Manager {ActorId} attempted to activate user {TargetId} who is neither Employee nor Customer. Rejected.",
                    currentUser.UserId, command.UserId);

                return Result.Failure
                    ("Managers may only activate Employee or Customer accounts. Activating other managers, administrators, or read-only users requires Administrator access.");
            }
        }

        // ------------------------------------------------------------------
        // 2. Load the user.
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("ActivateUser: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 3. Defensive guard: don't let a user activate themselves if they
        //    are the current user (they're already active if they're
        //    making the call). This isn't a security issue but it would
        //    produce a misleading audit log. We allow it but log a warning.
        // ------------------------------------------------------------------
        if (user.Id == currentUser.UserId)
        {
            logger.LogWarning
                ("ActivateUser: user {ActorId} attempted to activate themselves. Allowing (no-op).",
                currentUser.UserId);

            return Result.Failure("You cannot activate yourself. You are already active.");
        }

        // ------------------------------------------------------------------
        // 4. Delegate to the aggregate. Activate is idempotent.
        //    This sets Domain.User.IsActive = true (in the Domain Users
        //    table). The change is tracked by EF Core and will be committed
        //    by SaveChangesAsync below.
        // ------------------------------------------------------------------
        user.Activate();

        // ------------------------------------------------------------------
        // 5. SYNC THE IDENTITY-SIDE IsActive FLAG.
        //
        //    Same bug-fix pattern as DeactivateUserCommandHandler. Without
        //    this call, ApplicationUser.IsActive (in AspNetUsers) stays at
        //    false (from when the user was deactivated), and the Login.razor
        //    pre-check would reject the user's next login attempt — making
        //    reactivation a no-op.
        //
        //    SetUserActiveStatusAsync also refreshes the SecurityStamp,
        //    which is a no-op for reactivation (user has no existing
        //    session to invalidate) but harmless + symmetric.
        // ------------------------------------------------------------------
        var identityResult = await userAccountService.SetUserActiveStatusAsync(
            command.UserId, isActive: true, cancellationToken);

        if (!identityResult.IsSuccess)
        {
            logger.LogWarning
                ("ActivateUser: SetUserActiveStatusAsync failed for user {UserId}. " +
                 "Domain-side Activate() will be rolled back with the next SaveChanges " +
                 "(no transaction commit). Errors: {Errors}. Requested by user {ActorId}.",
                command.UserId, identityResult.Error ?? "unknown", currentUser.UserId);

            return identityResult;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("ActivateUser: user {UserId} (worker ID '{WorkerId}') activated by user {ActorId}. " +
             "Both Domain and Identity IsActive flags set to true.",
            user.Id, user.WorkerId, currentUser.UserId);

        return Result.Success();
    }
}
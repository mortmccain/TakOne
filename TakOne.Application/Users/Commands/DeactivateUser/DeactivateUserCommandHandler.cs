using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.DeactivateUser;

/// <summary>
/// Soft-deletes a user (sets IsActive = false).
///
/// <b>DOMAIN + IDENTITY SYNC (the "deactivated user can still log in" bug fix):</b>
/// <para>
/// This handler updates BOTH the Domain <c>User.IsActive</c> flag (via the
/// aggregate's <c>Deactivate()</c> method) AND the Identity
/// <c>ApplicationUser.IsActive</c> flag (via
/// <c>IUserAccountService.SetUserActiveStatusAsync</c>). The Identity-side
/// update is what makes the <c>Login.razor</c> pre-check actually work —
/// without it, <c>UserManager.FindByNameAsync(workerId).IsActive</c> reads
/// from <c>AspNetUsers</c> and is still <c>true</c>, so a freshly deactivated
/// user could log in from any device, anywhere, with their original password.
/// </para>
/// <para>
/// The Identity-side call ALSO refreshes the <c>SecurityStamp</c>, which
/// invalidates any outstanding auth cookies for the user. The
/// <c>SecurityStampValidator</c> (configured with a 30s validation interval)
/// will reject the user's existing session within 30s — they get redirected
/// to login on their next request.
/// </para>
/// <para>
/// Both updates happen in the same EF Core transaction (both repos share
/// <c>ApplicationDbContext</c>), so the Domain and Identity rows stay
/// consistent even if one of the writes fails.
/// </para>
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
        IUserAccountService userAccountService,
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
        //    This sets Domain.User.IsActive = false (in the Domain Users
        //    table). The change is tracked by EF Core and will be committed
        //    by SaveChangesAsync below.
        // ------------------------------------------------------------------
        user.Deactivate();

        // ------------------------------------------------------------------
        // 5. SYNC THE IDENTITY-SIDE IsActive FLAG.
        //
        //    This is the bug fix for "deactivated user can still log in".
        //    Without this call, ApplicationUser.IsActive (in AspNetUsers)
        //    stays at its default value of true, and Login.razor's pre-check
        //    `if (appUser is null || !appUser.IsActive)` passes — letting
        //    the user log in from any device, anywhere.
        //
        //    SetUserActiveStatusAsync ALSO refreshes the SecurityStamp,
        //    which invalidates any outstanding auth cookies for the user
        //    (kicking their existing sessions out within 30s via the
        //    SecurityStampValidator).
        //
        //    We do this BEFORE SaveChangesAsync so that if the Identity
        //    update fails, the Domain-side mutation is also rolled back
        //    (they share the same DbContext + transaction). If we did it
        //    AFTER SaveChangesAsync, a failure here would leave the Domain
        //    User deactivated but the Identity User still active — an
        //    inconsistent state.
        // ------------------------------------------------------------------
        var identityResult = await userAccountService.SetUserActiveStatusAsync(
            command.UserId, isActive: false, cancellationToken);

        if (!identityResult.IsSuccess)
        {
            logger.LogWarning
                ("DeactivateUser: SetUserActiveStatusAsync failed for user {UserId}. " +
                 "Domain-side Deactivate() will be rolled back with the next SaveChanges " +
                 "(no transaction commit). Errors: {Errors}. Requested by user {ActorId}.",
                command.UserId, identityResult.Error ?? "unknown", currentUser.UserId);

            return identityResult;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("DeactivateUser: user {UserId} (worker ID '{WorkerId}') deactivated by user {ActorId}. " +
             "Both Domain and Identity IsActive flags set to false; SecurityStamp refreshed.",
            user.Id, user.WorkerId, currentUser.UserId);

        return Result.Success();
    }
}
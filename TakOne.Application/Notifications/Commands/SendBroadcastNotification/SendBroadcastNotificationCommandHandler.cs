using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Errors;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Commands.SendBroadcastNotification;

/// <summary>
/// Handler for <see cref="SendBroadcastNotificationCommand"/>. Resolves
/// the recipient audience from the scope, fans out per-user Notification
/// rows, and persists everything (audit row + N fanout rows) in the same
/// EF Core transaction via Wolverine's AutoApplyTransactions.
/// </summary>
/// <remarks>
/// <para>
/// <b>AUTHORIZATION</b>: defense-in-depth auth check at the top of the
/// handler re-verifies the caller is an Admin. The
/// <c>[RequireRoles(Admin)]</c> attribute on the command should already
/// reject non-admins via Wolverine's <c>AuthorizationPolicyVerifier</c>
/// middleware; this is the second layer.
/// </para>
/// <para>
/// <b>DELEGATES FANOUT</b> to <see cref="BroadcastFanout.ExecuteAsync"/>
/// — a shared helper that also runs the system-emitted app-update
/// broadcast (via <c>EmitAppUpdateBroadcastCommandHandler</c>). This
/// keeps the transactional fanout logic in exactly one place.
/// </para>
/// <para>
/// <b>RETURN VALUE</b>: <c>Result&lt;int&gt;</c> — the recipient count.
/// The UI shows "Broadcast sent to N users" using this.
/// </para>
/// <para>
/// <b>STABLE ERROR CODES</b>: returns culture-neutral codes from
/// <see cref="NotificationErrors"/> (NOT hardcoded English) so the UI
/// layer can localize per the user's <c>CurrentUICulture</c>. Matches
/// the project convention.
/// </para>
/// </remarks>
public sealed class SendBroadcastNotificationCommandHandler
{
    public static async Task<Result<int>> HandleAsync(
        SendBroadcastNotificationCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        ICustomerGroupRepository groupRepository,
        IBroadcastNotificationRepository broadcastRepository,
        INotificationRepository notificationRepository,
        IUnitOfWork unitOfWork,
        ILogger<SendBroadcastNotificationCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // ── Defense-in-depth auth check. ──
        // The [RequireRoles(Admin)] attribute on the command already
        // enforces this via Wolverine's AuthorizationPolicyVerifier; this
        // is the second layer (catches a misconfigured middleware).
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty
            || !currentUser.IsInRole(Roles.Admin))
        {
            return Result<int>.Failure(NotificationErrors.FormatBroadcastAuthRequired());
        }

        // ── Scope=Group: validate the target group exists (defense-in-depth). ──
        // The validator checked TargetGroupId is non-empty; here we verify
        // the group actually exists in the DB. If it doesn't, return a
        // clean error code (the admin sees "Group not found" in their UI).
        if (command.Scope == BroadcastScope.Group && command.TargetGroupId.HasValue)
        {
            var group = await groupRepository.GetByIdReadOnlyAsync(
                command.TargetGroupId.Value, cancellationToken);
            if (group is null)
            {
                return Result<int>.Failure(NotificationErrors.FormatBroadcastGroupNotFound());
            }
        }

        // ── Scope=User: validate the target user exists + is active. ──
        // The fanout helper handles the empty case (returns RecipientCount=0),
        // but here we surface a clean error if the user doesn't exist OR is
        // inactive — the admin should know they tried to broadcast to a
        // non-existent/deactivated user.
        if (command.Scope == BroadcastScope.User && command.TargetUserId.HasValue)
        {
            var user = await userRepository.GetByIdAsync(
                command.TargetUserId.Value, cancellationToken);
            if (user is null)
            {
                return Result<int>.Failure(NotificationErrors.FormatBroadcastUserNotFound());
            }
            if (!user.IsActive)
            {
                return Result<int>.Failure(NotificationErrors.FormatBroadcastUserInactive());
            }
        }

        // ── Delegate to the shared fanout helper. ──
        // The helper resolves recipients, creates the audit row + N fanout
        // rows, and persists them all atomically in this handler's EF Core
        // transaction (Wolverine's AutoApplyTransactions).
        var recipientCount = await BroadcastFanout.ExecuteAsync(
            sentByUserId: currentUser.UserId,
            scope: command.Scope,
            targetRoleName: command.TargetRoleName,
            targetGroupId: command.TargetGroupId,
            targetUserId: command.TargetUserId,
            title: command.Title,
            message: command.Message,
            fanoutKind: NotificationKind.Broadcast, // admin-authored = Broadcast kind
            userRepository: userRepository,
            broadcastRepository: broadcastRepository,
            notificationRepository: notificationRepository,
            unitOfWork: unitOfWork,
            logger: logger,
            cancellationToken: cancellationToken);

        return Result<int>.Success(recipientCount);
    }
}

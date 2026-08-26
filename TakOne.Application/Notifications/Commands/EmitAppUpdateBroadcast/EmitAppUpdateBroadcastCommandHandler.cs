using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Errors;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Commands.EmitAppUpdateBroadcast;

/// <summary>
/// Handler for <see cref="EmitAppUpdateBroadcastCommand"/>. System-emitted
/// "app updated" broadcast at startup. Fans out to every active user with
/// <see cref="NotificationKind.AppUpdate"/>, recording
/// <see cref="Domain.Notifications.Entities.BroadcastNotification.SentByUserId"/>
/// = <see cref="Guid.Empty"/> (no human author).
/// </summary>
/// <remarks>
/// <para>
/// <b>DELEGATES FANOUT</b> to <see cref="BroadcastFanout.ExecuteAsync"/>
/// — the same shared helper the admin-authored
/// <c>SendBroadcastNotificationCommand</c> uses. This keeps the
/// transactional fanout logic (resolve recipients → create audit row →
/// create N fanout rows → persist atomically) in exactly one place.
/// </para>
/// <para>
/// <b>SCOPE=All, ALL TARGETS NULL</b>: hard-coded — app-update
/// notifications reach every active user. No need for scope-target
/// consistency validation; the inputs are always valid by construction.
/// </para>
/// <para>
/// <b>SENT BY USER ID = Guid.Empty</b>: this is the marker that the
/// <c>BroadcastNotificationDto.SentByUserName</c> resolver uses to know
/// "this is a system broadcast" (it returns null for the user name instead
/// of trying to look up Guid.Empty in the user table).
/// </para>
/// <para>
/// <b>BEST-EFFORT FAILURE HANDLING</b>: if this handler throws (e.g. DB
/// is unreachable), Wolverine's retry policy may re-attempt, but
/// ultimately the failure is logged and the app continues to boot. The
/// hosted service that dispatched this command has its own try/catch
/// wrapper — a broadcast failure must NEVER prevent the app from starting.
/// Users who miss the broadcast will see the new version's UI on next
/// page load regardless (the running code is the new code).
/// </para>
/// </remarks>
public sealed class EmitAppUpdateBroadcastCommandHandler
{
    public static async Task<Result<int>> HandleAsync(
        EmitAppUpdateBroadcastCommand command,
        IUserRepository userRepository,
        IBroadcastNotificationRepository broadcastRepository,
        INotificationRepository notificationRepository,
        IUnitOfWork unitOfWork,
        ILogger<EmitAppUpdateBroadcastCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // System-emitted: SentByUserId = Guid.Empty, Scope = All, all
        // targets null, FanoutKind = AppUpdate. The fanout helper resolves
        // all active users as recipients.
        var recipientCount = await BroadcastFanout.ExecuteAsync(
            sentByUserId: Guid.Empty, // system — no human author
            scope: Domain.Notifications.Enums.BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: command.Title,
            message: command.Message,
            fanoutKind: NotificationKind.AppUpdate,
            userRepository: userRepository,
            broadcastRepository: broadcastRepository,
            notificationRepository: notificationRepository,
            unitOfWork: unitOfWork,
            logger: logger,
            cancellationToken: cancellationToken);

        logger.LogInformation(
            "AppUpdate broadcast fanned out to {Count} recipient(s). Title: {Title}",
            recipientCount, command.Title);

        return Result<int>.Success(recipientCount);
    }
}

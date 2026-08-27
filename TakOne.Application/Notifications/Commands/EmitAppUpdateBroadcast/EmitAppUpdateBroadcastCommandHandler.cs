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
/// <b>IDEMPOTENCY DEDUP (Wolverine redelivery guard)</b>: before fanning
/// out, the handler checks whether a <c>BroadcastNotification</c> audit
/// row with the SAME <c>Title</c> + <c>FanoutKind=AppUpdate</c> already
/// exists. If yes, it skips the fanout and returns the original
/// <c>RecipientCount</c>. This prevents duplicate "app updated to vX.Y.Z"
/// notifications when Wolverine's durable outbox redelivers an unacked
/// <c>EmitAppUpdateBroadcastCommand</c> (e.g. process crash between
/// SaveChanges commit and the worker ack). The title is a safe dedup key
/// because the hosted service composes it deterministically from the
/// assembly version — a redelivered message has the same title, while a
/// legitimately-new broadcast has a different one.
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
        // ── IDEMPOTENCY DEDUP ──
        // Wolverine's durable outbox may redeliver this command if the
        // process crashed between the SaveChanges commit and the worker
        // ack. Without this check, a redelivery would create a SECOND
        // audit row + a SECOND set of per-user fanout rows → every user
        // would see duplicate "app updated" notifications.
        //
        // The title is a safe dedup key: the hosted service composes it
        // deterministically from AssemblyInformationalVersion
        // ("TakOne updated to v{newVersion}"). A redelivered message has
        // the SAME title; a legitimately-new broadcast has a DIFFERENT
        // title (different newVersion → different title).
        //
        // Edge case: same version deployed → rolled back → redeployed.
        // After the first deploy, persistedVersion == newVersion. On
        // rollback (old image), persistedVersion stays newVersion but
        // assemblyVersion is oldVersion → broadcast "updated from
        // {newVersion} to {oldVersion}" (DIFFERENT title → no dedup hit,
        // correctly announces the rollback as an "update"). On redeploy
        // of newVersion, persistedVersion == newVersion == assemblyVersion
        // → NO broadcast (the hosted service short-circuits before
        // dispatching). So the dedup key is safe across rollback scenarios.
        var existing = await broadcastRepository.GetByTitleAndKindAsync(
            command.Title,
            NotificationKind.AppUpdate,
            cancellationToken);

        if (existing is not null)
        {
            logger.LogInformation(
                "AppUpdate broadcast with title '{Title}' already exists (BroadcastId={BroadcastId}, RecipientCount={Count}) — skipping fanout (idempotent dedup, likely a Wolverine redelivery).",
                command.Title, existing.Id, existing.RecipientCount);

            // Return the original recipient count so the caller's success
            // log reflects reality. The hosted service doesn't inspect the
            // return value, but returning the honest count keeps the audit
            // trail consistent.
            return Result<int>.Success(existing.RecipientCount);
        }

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

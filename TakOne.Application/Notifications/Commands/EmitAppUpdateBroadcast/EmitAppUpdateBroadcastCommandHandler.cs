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
/// <b>INPUT-LENGTH VALIDATION (Brutal Code Review v3 #30, Round 18-C):</b>
/// defense-in-depth length limits on <c>Title</c> (≤ 200 chars) and
/// <c>Message</c> (≤ 2000 chars). The system-internal caller
/// (<c>AppUpdateBroadcasterHostedService</c>) composes both from the
/// assembly version, so the inputs are always well-formed in production.
/// BUT: a future code path that exposes <c>IMessageBus</c> to a Blazor
/// component, or a developer manually composing the command for testing,
/// could supply arbitrarily long strings — without these limits a 100MB
/// Title or Message would be persisted to <c>BroadcastNotification</c>
/// + N fanout <c>Notification</c> rows (one per active user) and easily
/// exhaust DB storage.
/// </para>
/// <para>
/// The check is implemented TWO ways (defense-in-depth pairing):
/// <list type="bullet">
///   <item><b>Wolverine pipeline:</b>
///   <c>EmitAppUpdateBroadcastCommandValidator</c> (FluentValidation)
///   runs automatically via Wolverine's <c>UseFluentValidation</c>
///   middleware BEFORE the handler — validation failures short-circuit
///   the handler entirely. This catches all Wolverine-dispatched
///   invocations.</item>
///   <item><b>In-handler check:</b> the handler ALSO checks the same
///   limits at the top of <c>HandleAsync</c> and returns
///   <c>Result.Failure</c> with the same error codes. This catches any
///   caller that bypasses Wolverine's pipeline (e.g. a direct handler
///   invocation from a test, or a future non-Wolverine dispatch path).
///   The double check is intentional — neither layer trusts the other.</item>
/// </list>
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
    /// <summary>
    /// Maximum length (in characters) of the broadcast Title. Must match
    /// <see cref="EmitAppUpdateBroadcastCommandValidator.MaxTitleLength"/>.
    /// </summary>
    private const int MaxTitleLength = 200;

    /// <summary>
    /// Maximum length (in characters) of the broadcast Message. Must match
    /// <see cref="EmitAppUpdateBroadcastCommandValidator.MaxMessageLength"/>.
    /// </summary>
    private const int MaxMessageLength = 2000;

    public static async Task<Result<int>> HandleAsync(
        EmitAppUpdateBroadcastCommand command,
        IUserRepository userRepository,
        IBroadcastNotificationRepository broadcastRepository,
        INotificationRepository notificationRepository,
        INotificationPreferenceRepository preferenceRepository,
        IUnitOfWork unitOfWork,
        ILogger<EmitAppUpdateBroadcastCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // ── INPUT-LENGTH VALIDATION (Brutal Code Review v3 #30, ───────
        //    Round 18-C) ───────────────────────────────────────────────
        //
        // Defense-in-depth: even though the system-internal caller
        // (AppUpdateBroadcasterHostedService) always composes well-formed
        // inputs, a future code path that exposes IMessageBus to a Blazor
        // component could supply arbitrarily long strings. Without these
        // limits, a 100MB Title or Message would be persisted to
        // BroadcastNotification + N fanout Notification rows (one per
        // active user) and exhaust DB storage.
        //
        // The FluentValidation validator
        // (EmitAppUpdateBroadcastCommandValidator) runs the SAME checks
        // via Wolverine's pipeline BEFORE this handler. The in-handler
        // check here catches any caller that bypasses Wolverine (direct
        // handler invocation from a test, future non-Wolverine dispatch).
        //
        // The check is BEFORE the dedup check + fanout — fail fast,
        // before any DB or network round-trip.
        if (string.IsNullOrWhiteSpace(command.Title))
        {
            logger.LogWarning(
                "EmitAppUpdateBroadcast: rejected — Title is empty or whitespace.");
            return Result<int>.Failure(
                NotificationErrors.FormatAppUpdateTitleRequired());
        }

        if (command.Title.Length > MaxTitleLength)
        {
            logger.LogWarning(
                "EmitAppUpdateBroadcast: rejected — Title length {Length} exceeds " +
                "the {MaxLength}-character limit. Title preview: '{Preview}'",
                command.Title.Length,
                MaxTitleLength,
                command.Title.Length > 80 ? command.Title[..80] + "…" : command.Title);
            return Result<int>.Failure(
                NotificationErrors.FormatAppUpdateTitleTooLong());
        }

        if (string.IsNullOrWhiteSpace(command.Message))
        {
            logger.LogWarning(
                "EmitAppUpdateBroadcast: rejected — Message is empty or whitespace.");
            return Result<int>.Failure(
                NotificationErrors.FormatAppUpdateMessageRequired());
        }

        if (command.Message.Length > MaxMessageLength)
        {
            logger.LogWarning(
                "EmitAppUpdateBroadcast: rejected — Message length {Length} exceeds " +
                "the {MaxLength}-character limit. Message preview: '{Preview}'",
                command.Message.Length,
                MaxMessageLength,
                command.Message.Length > 80 ? command.Message[..80] + "…" : command.Message);
            return Result<int>.Failure(
                NotificationErrors.FormatAppUpdateMessageTooLong());
        }

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
            preferenceRepository: preferenceRepository,
            unitOfWork: unitOfWork,
            logger: logger,
            cancellationToken: cancellationToken);

        logger.LogInformation(
            "AppUpdate broadcast fanned out to {Count} recipient(s). Title: {Title}",
            recipientCount, command.Title);

        return Result<int>.Success(recipientCount);
    }
}

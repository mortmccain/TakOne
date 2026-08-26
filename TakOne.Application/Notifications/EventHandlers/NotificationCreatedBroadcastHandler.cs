using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Events;

namespace TakOne.Application.Notifications.EventHandlers;

/// <summary>
/// Wolverine handler for <see cref="NotificationCreatedDomainEvent"/>.
/// Bridges the persisted Notification row (created by a sale-lifecycle
/// handler like <see cref="TakOne.Application.Sales.EventHandlers.NotifyOnSaleApprovedEventHandler"/>)
/// to the real-time UI: pings the recipient's SignalR connection so their
/// bell badge + notification list refresh live.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A DEDICATED BROADCAST HANDLER</b> (vs. calling the broadcaster
/// directly from each sale-lifecycle handler): decoupling. Tomorrow, when
/// a non-sale Notification-creating path is added (e.g. "your account was
/// created", "your password was reset"), that path simply calls
/// <c>Notification.Create(...)</c> + <c>repo.AddAsync(...)</c> — the
/// broadcast is triggered automatically by the
/// <see cref="NotificationCreatedDomainEvent"/> raised in the aggregate's
/// factory. The creator doesn't need to remember to call the broadcaster;
/// the architecture enforces "every Notification gets a SignalR ping"
/// by construction.
/// </para>
/// <para>
/// <b>TRANSACTIONAL SEMANTICS</b>: this handler runs asynchronously AFTER
/// the originating transaction commits. Wolverine's transactional outbox
/// writes the <see cref="NotificationCreatedDomainEvent"/> message to the
/// <c>wolverine_messages</c> table atomically with the originating
/// transaction's SaveChangesAsync. If the originating transaction rolls
/// back, the outbox entry rolls back too — this handler never runs. The
/// broadcast is eventual-consistent with the notification row (typical lag:
/// milliseconds).
/// </para>
/// <para>
/// <b>BEST-EFFORT</b>: the broadcast is wrapped in try/catch. Failures are
/// logged but never propagate — the persisted Notification row is the
/// source of truth. If the live ping fails (user's circuit is down, hub
/// is restarting), the user simply sees the notification on next page load.
/// </para>
/// <para>
/// <b>NO AUTHORIZATION ATTRIBUTE NEEDED</b>: this is an EVENT HANDLER, not
/// a user-dispatched message. The <c>AuthorizationPolicyVerifier</c> only
/// scans types whose name ends with "Command" or "Query" — event handlers
/// are not user-initiated. The user's auth was already enforced by the
/// originating command (e.g. <c>ApproveSaleCommand</c>'s
/// <c>[RequireRoles(...)]</c>).
/// </para>
/// </remarks>
public sealed class NotificationCreatedBroadcastHandler
{
    public static async Task HandleAsync(
        NotificationCreatedDomainEvent @event,
        INotificationBroadcaster broadcaster,
        ILogger<NotificationCreatedBroadcastHandler> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await broadcaster.BroadcastToUserAsync(@event.UserId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort — the persisted Notification row is the source
            // of truth. The user will see it on next page load if the
            // live ping fails.
            logger.LogWarning(ex,
                "NotificationCreated broadcast to user {UserId} failed (notification persisted anyway).",
                @event.UserId);
        }
    }
}

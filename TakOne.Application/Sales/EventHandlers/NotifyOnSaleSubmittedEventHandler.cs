using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Domain.Sales.Events;

namespace TakOne.Application.Sales.EventHandlers;

/// <summary>
/// Wolverine handler for <see cref="SaleSubmittedDomainEvent"/>.
/// Creates <see cref="Notification"/> rows for the SCOPED recipients:
///   1. The customer (their order was received and is awaiting approval).
///   2. The creator (the staff member who submitted the order on behalf
///      of the customer — gets a "you submitted" record).
/// </summary>
/// <remarks>
/// <para>
/// <b>TRANSACTIONAL SEMANTICS</b>: Wolverine's transactional outbox
/// writes the <see cref="SaleSubmittedDomainEvent"/> message to the
/// <c>wolverine_messages</c> table atomically with the originating
/// <c>SubmitSaleCommandHandler</c>'s <c>SaveChangesAsync</c>. This
/// handler then runs asynchronously in its OWN EF Core transaction
/// (created by Wolverine's transactional middleware) to persist the
/// Notification row.
/// </para>
/// <para>
/// <b>NO FALSE-NOTIFICATION GUARANTEE</b>: if the originating Submit
/// transaction rolls back (e.g. retry exhausted, aggregate invariant
/// violated), the outbox entry rolls back too — this handler never
/// runs. The user never receives a false notification for a sale that
/// didn't actually transition. The notification is eventual-consistent
/// with the sale change (typical lag: milliseconds).
/// </para>
/// <para>
/// <b>DEDUPLICATION</b>: the unique index on (UserId, SaleId, Kind)
/// catches any duplicate event (Wolverine's outbox is at-least-once).
/// On duplicate INSERT, the unique-constraint violation is caught by
/// <see cref="IUnitOfWork.ExecuteWithRetryAsync"/> semantics and the
/// handler idempotently skips. The pre-INSERT <c>ExistsAsync</c> check
/// avoids the wasted round-trip in the common case.
/// </para>
/// <para>
/// <b>SCOPING (the user's exact spec)</b>: only the customer + the
/// creator receive notifications. NO broadcast to all staff — "if they
/// get notifications for everything their inbox will explode" (user).
/// Staff who want to see pending-approval queue use the dashboard, not
/// the notifications inbox.
/// </para>
/// <para>
/// <b>SELF-BUY SHORT-CIRCUIT</b>: when <c>CreatedByUserId == CustomerId</c>
/// (self-buy — customer created and submitted their own cart), the
/// customer IS the creator. We create ONE notification, not two duplicate
/// ones for the same user.
/// </para>
/// <para>
/// <b>BROADCAST IS DECOUPLED</b>: this handler persists the Notification
/// row only. The SignalR broadcast to the recipient's UI is handled by a
/// SEPARATE handler (<c>NotificationCreatedBroadcastHandler</c>) that
/// subscribes to the <see cref="NotificationCreatedDomainEvent"/> raised
/// by <see cref="Notification.Create"/>. This decouples the creator from
/// the broadcaster — tomorrow, when a non-sale Notification-creating path
/// is added (account-created, password-reset), it just creates a
/// Notification row and the broadcast is triggered automatically.
/// </para>
/// <para>
/// <b>NO AUTHORIZATION ATTRIBUTE NEEDED</b>: this is an EVENT HANDLER,
/// not a user-dispatched message. The <c>AuthorizationPolicyVerifier</c>
/// only scans types whose name ends with "Command" or "Query" — event
/// handlers are not user-initiated. The user's auth was already enforced
/// by the originating <c>SubmitSaleCommand</c>'s <c>[RequireRoles(...)]</c>.
/// </para>
/// </remarks>
public sealed class NotifyOnSaleSubmittedEventHandler
{
    public static async Task HandleAsync(
        SaleSubmittedDomainEvent @event,
        INotificationRepository notificationRepository,
        INotificationPreferenceRepository preferenceRepository,
        IUnitOfWork unitOfWork,
        ILogger<NotifyOnSaleSubmittedEventHandler> logger,
        CancellationToken cancellationToken)
    {
        // IUnitOfWork is injected as a marker so Wolverine's
        // AutoApplyTransactions policy enrolls this handler in an EF Core
        // transaction. We do NOT call unitOfWork.SaveChangesAsync here —
        // Wolverine's transactional middleware calls it after the handler
        // returns, persisting the new Notification row(s) atomically.

        // ── 1. Notify the customer ("your order was received"). ──
        await CreateForUserIfNotExistsAsync(
            userId: @event.CustomerId,
            kind: NotificationKind.SaleSubmitted,
            saleId: @event.SaleId,
            saleDisplayNumber: @event.SaleNumber?.Value,
            actorName: null, // self-buy: no actor name needed
            notificationRepository: notificationRepository,
            preferenceRepository: preferenceRepository,
            logger: logger,
            cancellationToken: cancellationToken);

        // ── 2. Notify the creator ("you submitted an order"). ──
        // Skip if the creator IS the customer (self-buy) — would create
        // a duplicate notification for the same user with the same kind.
        if (@event.CreatedByUserId != @event.CustomerId)
        {
            await CreateForUserIfNotExistsAsync(
                userId: @event.CreatedByUserId,
                kind: NotificationKind.SaleSubmitted,
                saleId: @event.SaleId,
                saleDisplayNumber: @event.SaleNumber?.Value,
                actorName: null,
                notificationRepository: notificationRepository,
                preferenceRepository: preferenceRepository,
                logger: logger,
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Shared helper: idempotently creates a notification for a user if
    /// no (userId, saleId, kind) tuple already exists. Does NOT call
    /// SaveChangesAsync — Wolverine's transactional middleware handles
    /// that. Does NOT broadcast — the
    /// <see cref="NotificationCreatedDomainEvent"/> raised by
    /// <see cref="Notification.Create"/> triggers the broadcast via the
    /// dedicated <c>NotificationCreatedBroadcastHandler</c>.
    /// </summary>
    private static async Task CreateForUserIfNotExistsAsync(
        Guid userId,
        NotificationKind kind,
        Guid saleId,
        string? saleDisplayNumber,
        string? actorName,
        INotificationRepository notificationRepository,
        INotificationPreferenceRepository preferenceRepository,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // MUTE SUPPRESSION (per-user notification preferences): a muted
        // kind skips row creation ENTIRELY — no Notification INSERT, no
        // NotificationCreatedDomainEvent, no SignalR ping. The suppression
        // happens HERE (creation time), not at read time, so muted kinds
        // never accumulate unread rows. Not retroactive — rows created
        // before the mute stay in the feed. Sparse default: no preference
        // row = not muted (single indexed seek).
        if (await preferenceRepository.IsMutedAsync(userId, kind, cancellationToken))
        {
            logger.LogDebug(
                "Notification ({Kind}, sale={SaleId}, user={UserId}) suppressed — kind muted by user.",
                kind, saleId, userId);
            return;
        }

        // Idempotency short-circuit: avoid a wasted INSERT+retry if the
        // event was redelivered. The unique index catches the race
        // anyway, but this avoids the round-trip in the common case.
        if (await notificationRepository.ExistsAsync(userId, saleId, kind, cancellationToken))
        {
            logger.LogDebug(
                "Notification ({Kind}, sale={SaleId}, user={UserId}) already exists — skipping (idempotent).",
                kind, saleId, userId);
            return;
        }

        var notification = Notification.Create(
            userId: userId,
            kind: kind,
            saleId: saleId,
            saleDisplayNumber: saleDisplayNumber,
            actorName: actorName,
            reason: null);

        // Notification.Create raises NotificationCreatedDomainEvent. Wolverine's
        // EF Core scraper picks it up at SaveChangesAsync time and writes
        // the event to the outbox. The NotificationCreatedBroadcastHandler
        // subscribes to that event and pings the SignalR hub — the
        // broadcast happens automatically, decoupled from this handler.
        await notificationRepository.AddAsync(notification, cancellationToken);
    }
}

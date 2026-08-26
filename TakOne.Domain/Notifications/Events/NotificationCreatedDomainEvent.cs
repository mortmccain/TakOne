using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Notifications.Events;

/// <summary>
/// Raised by <see cref="Entities.Notification.Create"/> when a new
/// notification is materialized. Wolverine's EF Core domain-event scraper
/// picks it up at the NotifyOn* handler's <c>SaveChangesAsync</c> time
/// and routes it through the transactional outbox — so a downstream
/// handler can ping the SignalR <c>NotificationHub</c> to refresh the
/// recipient's UI. The broadcast is decoupled from the notification's
/// creator (see class remarks).
/// </summary>
/// <remarks>
/// <para>
/// <b>TRANSACTIONAL INVARIANT</b>: Wolverine's EF Core domain-event scraper
/// pulls this event off the <see cref="Entities.Notification"/> aggregate
/// at the NotifyOn* handler's <c>SaveChangesAsync</c> time and writes the
/// event message to the <c>wolverine_messages</c> outbox table atomically
/// with the Notification row's INSERT. If the NotifyOn* handler's
/// transaction rolls back, the outbox entry rolls back too — the
/// <c>NotificationCreatedBroadcastHandler</c> never runs. No false SignalR
/// ping reaches the UI.
/// </para>
/// <para>
/// <b>BROADCAST IS A SEPARATE EVENT HANDLER</b>: a dedicated handler
/// (<c>NotificationCreatedBroadcastHandler</c> in Application) subscribes
/// to this event and calls <c>INotificationBroadcaster.BroadcastToUserAsync</c>
/// to ping the recipient's SignalR group. Application stays UI-tech-agnostic
/// (the <c>INotificationBroadcaster</c> interface is in Application; the
/// <c>SignalRNotificationBroadcaster</c> impl is in WebUI).
/// </para>
/// <para>
/// <b>DECOUPLING BENEFIT</b>: this event lets future Notification-creating
/// paths (account-created, password-reset, etc.) trigger a broadcast
/// automatically by simply calling <see cref="Entities.Notification.Create"/>
/// + <c>repo.AddAsync(...)</c>. The creator doesn't need to remember to
/// call the broadcaster; the architecture enforces "every Notification
/// gets a SignalR ping" by construction.
/// </para>
/// <para>
/// <b>STRUCTURED-ONLY PAYLOAD</b>: the event carries only the
/// discriminator + sale identifier (NO pre-localized title/message). The
/// recipient's UI localizes at render time using the kind + structured
/// fields. This means a language switch by the recipient instantly
/// re-renders all historical notifications in the new language.
/// </para>
/// </remarks>
public sealed class NotificationCreatedDomainEvent : BaseDomainEvent
{
    /// <summary>
    /// The Id of the <see cref="Entities.Notification"/> that was created.
    /// </summary>
    public Guid NotificationId { get; }

    /// <summary>
    /// The Id of the user who should see this notification (the
    /// notification's <c>UserId</c>). The broadcast handler uses this to
    /// address a SignalR message to the user's personal group
    /// (<c>user:{userId}</c>).
    /// </summary>
    public Guid UserId { get; }

    /// <summary>
    /// The discriminator that lets the broadcast handler shape the
    /// real-time payload (e.g. include the right icon name). Persisted as
    /// int — the enum value of <c>NotificationKind</c>.
    /// </summary>
    public int Kind { get; }

    /// <summary>
    /// The display-safe identifier of the sale this notification is about
    /// (e.g. <c>INT-1505-00000042</c>). May be null for non-sale
    /// notifications (future).
    /// </summary>
    public string? SaleDisplayNumber { get; }

    public NotificationCreatedDomainEvent(
        Guid notificationId,
        Guid userId,
        int kind,
        string? saleDisplayNumber)
    {
        NotificationId = notificationId;
        UserId = userId;
        Kind = kind;
        SaleDisplayNumber = saleDisplayNumber;
    }
}

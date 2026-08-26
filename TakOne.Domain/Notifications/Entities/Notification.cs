using TakOne.Domain.Notifications.Enums;
using TakOne.Domain.Notifications.Events;
using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Notifications.Entities;

/// <summary>
/// Aggregate root for a single user-targeted notification row.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Notification"/> is created by a Wolverine event handler
/// that subscribes to the originating domain event (e.g.
/// <c>SaleApprovedDomainEvent</c> → <c>NotifyOnSaleApprovedEventHandler</c>).
/// The handler runs in its OWN EF Core transaction (created by Wolverine's
/// transactional middleware) — the originating transaction's
/// <c>SaveChangesAsync</c> wrote the domain event to the outbox atomically
/// with the business mutation. If the originating transaction rolls back,
/// the outbox entry rolls back too and the NotifyOn* handler never runs —
/// no false notification reaches the user. The notification row is
/// eventual-consistent with the sale change (typical lag: milliseconds).
/// </para>
/// <para>
/// <b>SCOPING INVARIANT</b>: a Notification is always targeted at exactly
/// one <see cref="UserId"/>. Customers see notifications for sales where
/// they are the customer (their own orders). Staff see notifications for
/// sales where they were the actor (created / approved / invoiced /
/// cancelled). The creating handler enforces this — it never broadcasts
/// "every sale to every staff member".
/// </para>
/// <para>
/// <b>DEDUPLICATION INVARIANT</b>: the table has a UNIQUE INDEX on
/// <c>(UserId, SaleId, Kind)</c>. If the Wolverine outbox ever redelivers
/// the same sale-lifecycle event (at-least-once delivery), the second
/// INSERT for the same (recipient, sale, kind) tuple fails with a
/// SQL Server 2627/2601 unique-constraint violation — caught by the
/// retry loop in the handler, which on retry observes the existing row
/// and idempotently skips. No duplicate notifications reach the user.
/// </para>
/// <para>
/// <b>LOCALIZATION STRATEGY</b>: this aggregate stores ONLY STRUCTURED
/// data (<see cref="Kind"/>, <see cref="SaleDisplayNumber"/>,
/// <see cref="ActorName"/>, <see cref="Reason"/>). NO pre-localized
/// title/message text is persisted — the UI layer localizes at render
/// time using <c>IStringLocalizer</c> + the <see cref="Kind"/> enum to
/// look up the template + format it with the structured params. This is
/// the enterprise pattern (matches how <c>SaleListItemDto.Status</c>
/// is already localized: the DTO exposes the raw enum/string, the UI's
/// resx files hold the localized labels).
/// </para>
/// <para>
/// <b>READ STATE</b>: <see cref="ReadAtUtc"/> is null until the user
/// dismisses the notification. Persisting read state in the row (rather
/// than in client-side localStorage) means the read state survives circuit
/// restarts, multi-device logins, and is consistent across mobile/PC.
/// </para>
/// </remarks>
public sealed class Notification : AggregateRoot
{
    // ── PROPERTIES ──────────────────────────────────────────────────────

    /// <summary>
    /// The user who should see this notification. Indexed.
    /// </summary>
    public Guid UserId { get; private set; }

    /// <summary>
    /// Discriminator — what kind of activity this is. Indexed.
    /// </summary>
    public NotificationKind Kind { get; private set; }

    /// <summary>
    /// The Id of the sale this notification is about. Indexed (with Kind
    /// in a unique composite). Null for future non-sale notifications.
    /// </summary>
    public Guid? SaleId { get; private set; }

    /// <summary>
    /// Display-safe sale identifier snapshot (e.g. "INT-1505-00000042"
    /// or "DRAFT-{Guid[0..8]}") so the UI doesn't have to re-query the
    /// sale to render the notification. Snapshotted at creation — if
    /// the sale is later voided, the historical notification text is
    /// preserved.
    /// </summary>
    public string? SaleDisplayNumber { get; private set; }

    /// <summary>
    /// The full name of the staff member who acted on the sale (the
    /// approver / invoicer / canceller), snapshot at create-time. Null
    /// for the customer-recipient path where the customer is themselves
    /// the actor (e.g. self-buy SaleSubmitted — no point naming
    /// themselves as the actor in their own notification).
    /// </summary>
    public string? ActorName { get; private set; }

    /// <summary>
    /// The cancellation reason — only set for <see cref="NotificationKind.SaleCancelled"/>.
    /// Null for all other kinds. The UI surfaces this as a sub-line in
    /// the cancelled notification.
    /// </summary>
    public string? Reason { get; private set; }

    /// <summary>
    /// UTC timestamp the notification was created. Indexed (most UIs list
    /// notifications newest-first via an ORDER BY CreatedAtUtc DESC).
    /// </summary>
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>
    /// UTC timestamp the user dismissed/read this notification. Null until
    /// <see cref="MarkAsRead"/> is called. Indexed so the "unread count"
    /// query (<c>WHERE UserId = @u AND ReadAtUtc IS NULL</c>) is a fast
    /// index seek.
    /// </summary>
    public DateTime? ReadAtUtc { get; private set; }

    // ── CONSTRUCTORS ────────────────────────────────────────────────────

#pragma warning disable CS8618
    /// <summary>
    /// Parameterless ctor for EF Core. DO NOT use from application code.
    /// </summary>
    private Notification() : base(Guid.Empty) { }
#pragma warning restore CS8618

    private Notification(
        Guid userId,
        NotificationKind kind,
        Guid? saleId,
        string? saleDisplayNumber,
        string? actorName,
        string? reason)
        : base(Guid.NewGuid())
    {
        EnsureUserIdValid(userId);

        UserId = userId;
        Kind = kind;
        SaleId = saleId;
        SaleDisplayNumber = saleDisplayNumber;
        ActorName = actorName;
        Reason = reason;
        CreatedAtUtc = DateTime.UtcNow;
        ReadAtUtc = null;
    }

    // ── FACTORY ─────────────────────────────────────────────────────────

    /// <summary>
    /// The ONLY way to construct a <see cref="Notification"/> from
    /// application code. Validates all fields and raises
    /// <see cref="NotificationCreatedDomainEvent"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>RAISES A DOMAIN EVENT</b> so Wolverine's EF Core scraper picks
    /// it up at SaveChangesAsync time and publishes through the
    /// transactional outbox. A separate broadcast handler subscribes to
    /// that event and pings the SignalR hub for the recipient's UI —
    /// all inside the same EF Core transaction as the triggering sale
    /// mutation. If the transaction rolls back, the event is never
    /// published; no false SignalR ping; no false UI notification.
    /// </para>
    /// </remarks>
    public static Notification Create(
        Guid userId,
        NotificationKind kind,
        Guid? saleId,
        string? saleDisplayNumber,
        string? actorName,
        string? reason = null)
    {
        var notification = new Notification(
            userId, kind, saleId, saleDisplayNumber, actorName, reason);

        notification.AddDomainEvent(new NotificationCreatedDomainEvent(
            notificationId: notification.Id,
            userId: notification.UserId,
            kind: (int)notification.Kind,
            saleDisplayNumber: notification.SaleDisplayNumber));

        return notification;
    }

    // ── BEHAVIOR ────────────────────────────────────────────────────────

    /// <summary>
    /// Marks the notification as read. Idempotent — calling it on an
    /// already-read notification is a no-op (no DomainException, no
    /// spurious UPDATE). This is intentional: the UI's "mark all read"
    /// action sends N <c>MarkAsRead</c> commands; idempotency means the
    /// Nth call on an already-read row doesn't fail.
    /// </summary>
    public void MarkAsRead()
    {
        if (ReadAtUtc is null)
        {
            ReadAtUtc = DateTime.UtcNow;
        }
    }

    // ── GUARDS ──────────────────────────────────────────────────────────

    private static void EnsureUserIdValid(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new DomainException(
                "A notification must be targeted at a non-empty user Id.");
        }
    }
}

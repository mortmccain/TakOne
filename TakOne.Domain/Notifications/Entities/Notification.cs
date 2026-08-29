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
    /// The admin-authored subject line, copied verbatim from the
    /// originating <see cref="BroadcastNotification"/>. Null for sale-lifecycle
    /// notifications (<see cref="NotificationKind.SaleSubmitted"/> etc.) —
    /// those use the structured <see cref="Kind"/> + <see cref="SaleDisplayNumber"/>
    /// + <see cref="ActorName"/> + <see cref="Reason"/> fields and localize
    /// at render time. Only set for <see cref="NotificationKind.Broadcast"/>
    /// and <see cref="NotificationKind.AppUpdate"/>.
    /// </summary>
    public string? Title { get; private set; }

    /// <summary>
    /// The admin-authored message body, copied verbatim from the
    /// originating <see cref="BroadcastNotification"/>. Null for sale-lifecycle
    /// notifications. Same nullness rules as <see cref="Title"/>.
    /// </summary>
    public string? Message { get; private set; }

    /// <summary>
    /// Foreign-key pointer to the <see cref="BroadcastNotification"/> aggregate
    /// that fanned out this per-user row. Null for sale-lifecycle notifications
    /// (they have no parent broadcast). Set for <see cref="NotificationKind.Broadcast"/>
    /// and <see cref="NotificationKind.AppUpdate"/> so the UI can group/correlate
    /// fanout rows back to their parent audit row.
    /// <para>
    /// <b>NO DB-LEVEL FK</b>: matches the existing convention (see
    /// <c>NotificationConfiguration</c> — "NO FK TO USERS / SALES").
    /// Cross-aggregate references are bare Guids, no navigation properties,
    /// no FKs at the DB level. The Application layer enforces the relationship.
    /// </para>
    /// </summary>
    public Guid? BroadcastId { get; private set; }

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
        string? reason,
        string? title,
        string? message,
        Guid? broadcastId)
        : base(Guid.NewGuid())
    {
        EnsureUserIdValid(userId);
        // Defense-in-depth: an undefined Kind (e.g. (NotificationKind)42 from a
        // mis-cast or bad data) would silently persist and later break the
        // UI's kind-based localization switch and the filtered unread-count
        // index. Both factories funnel through this ctor, so a single guard
        // covers all creation paths.
        if (!Enum.IsDefined(kind))
            throw new DomainException(
                $"'{kind}' is not a valid {nameof(NotificationKind)} value.");

        UserId = userId;
        Kind = kind;
        SaleId = saleId;
        SaleDisplayNumber = saleDisplayNumber;
        ActorName = actorName;
        Reason = reason;
        Title = title;
        Message = message;
        BroadcastId = broadcastId;
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
        // Sale-lifecycle factory: Title/Message/BroadcastId are null —
        // sale notifications use structured fields + UI-side localization.
        var notification = new Notification(
            userId, kind, saleId, saleDisplayNumber, actorName, reason,
            title: null, message: null, broadcastId: null);

        notification.AddDomainEvent(new NotificationCreatedDomainEvent(
            notificationId: notification.Id,
            userId: notification.UserId,
            kind: (int)notification.Kind,
            saleDisplayNumber: notification.SaleDisplayNumber));

        return notification;
    }

    /// <summary>
    /// Factory for broadcast fanout rows. Creates a per-user Notification
    /// that carries the admin-authored <paramref name="title"/> +
    /// <paramref name="message"/> verbatim, plus a back-pointer to the
    /// parent <see cref="BroadcastNotification"/> aggregate via
    /// <paramref name="broadcastId"/>.
    /// </summary>
    /// <param name="userId">The recipient user's Id.</param>
    /// <param name="kind">
    /// Must be <see cref="NotificationKind.Broadcast"/> or
    /// <see cref="NotificationKind.AppUpdate"/>. The factory enforces this —
    /// sale-lifecycle kinds cannot be created via this path (they must go
    /// through <see cref="Create"/> so their structured fields are populated
    /// by the sale-event handler, not free-form text from an admin).
    /// </param>
    /// <param name="broadcastId">
    /// The parent <see cref="BroadcastNotification"/> aggregate's Id.
    /// Must be non-empty.
    /// </param>
    /// <param name="title">The broadcast's subject line (1–200 chars).</param>
    /// <param name="message">The broadcast's body (1–1000 chars).</param>
    /// <remarks>
    /// <para>
    /// <b>TRANSACTIONAL INVARIANT</b>: called by
    /// <c>SendBroadcastNotificationCommandHandler</c> in the same EF Core
    /// transaction as the parent <c>BroadcastNotification</c> row's INSERT.
    /// Wolverine's <c>AutoApplyTransactions</c> middleware enrolls the
    /// handler in one transaction; if SaveChangesAsync fails, all N
    /// fanout rows + the audit row roll back together. No partial broadcast.
    /// </para>
    /// <para>
    /// <b>RAISES NotificationCreatedDomainEvent</b> — same as the sale-lifecycle
    /// factory. The existing <c>NotificationCreatedBroadcastHandler</c>
    /// subscribes and pings SignalR for the recipient's UI. The broadcast
    /// pipeline reuses the existing real-time infrastructure: no new SignalR
    /// channels, no new broadcaster methods. Each recipient's bell badge
    /// lights up the moment the fanout transaction commits.
    /// </para>
    /// <para>
    /// <b>NOT SUBJECT TO THE DEDUP UNIQUE INDEX</b>: see
    /// <see cref="NotificationKind.Broadcast"/>'s doc — the
    /// <c>UX_Notifications_UserId_SaleId_Kind</c> index is filtered to
    /// <c>WHERE SaleId IS NOT NULL</c>, and broadcast fanout rows have
    /// <c>SaleId IS NULL</c>. So each fanout INSERT is a distinct row, no
    /// dedup collision.
    /// </para>
    /// </remarks>
    public static Notification CreateBroadcast(
        Guid userId,
        NotificationKind kind,
        Guid broadcastId,
        string? title,
        string? message)
    {
        EnsureBroadcastKindValid(kind);
        EnsureBroadcastIdValid(broadcastId);

        var notification = new Notification(
            userId, kind,
            saleId: null, saleDisplayNumber: null, actorName: null, reason: null,
            title: title, message: message, broadcastId: broadcastId);

        notification.AddDomainEvent(new NotificationCreatedDomainEvent(
            notificationId: notification.Id,
            userId: notification.UserId,
            kind: (int)notification.Kind,
            saleDisplayNumber: null));

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

    private static void EnsureBroadcastKindValid(NotificationKind kind)
    {
        // Only Broadcast and AppUpdate can carry admin-authored title/message.
        // Sale-lifecycle kinds must go through the Create() factory so their
        // structured fields (SaleId, SaleDisplayNumber, ActorName) are populated
        // by the sale-event handler — never free-form text from an admin.
        if (kind != NotificationKind.Broadcast && kind != NotificationKind.AppUpdate)
        {
            throw new DomainException(
                $"CreateBroadcast requires NotificationKind.Broadcast or AppUpdate (got {kind}). " +
                "Sale-lifecycle kinds must use the Create() factory.");
        }
    }

    private static void EnsureBroadcastIdValid(Guid broadcastId)
    {
        if (broadcastId == Guid.Empty)
        {
            throw new DomainException(
                "A broadcast fanout Notification must reference a non-empty BroadcastId.");
        }
    }
}

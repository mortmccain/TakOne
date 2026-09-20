using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Repository abstraction for the <see cref="Notification"/> aggregate.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY AN INTERFACE IN APPLICATION</b>: keeps the Application layer
/// persistence-agnostic (clean architecture / dependency inversion). The
/// EF Core implementation lives in Infrastructure. Handlers in Application
/// depend on this interface, never on <c>ApplicationDbContext</c>.
/// </para>
/// <para>
/// <b>SCOPE</b>: all read methods filter by <c>userId</c> — the caller's
/// responsibility is to pass the correct user Id (typically
/// <c>ICurrentUserService.UserId</c>). The repository does NOT do
/// role-based scoping — that's the handler's job (it knows the role
/// policy). The repository only ensures that, given a userId, all
/// returned notifications belong to that user.
/// </para>
/// </remarks>
public interface INotificationRepository
{
    /// <summary>
    /// Returns a paginated slice of a user's notifications, newest-first.
    /// Pass <c>unreadOnly: true</c> to filter out read notifications (for
    /// the unread-only segmented view in the UI). Pass a non-null
    /// <paramref name="kind"/> to further restrict the page to that
    /// notification kind (Round 4 — the per-kind filter tabs; null = all
    /// kinds).
    /// </summary>
    Task<PaginatedResult<Notification>> GetPaginatedForUserAsync(
        Guid userId,
        int pageNumber,
        int pageSize,
        bool unreadOnly,
        NotificationKind? kind = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// HARD-DELETES one of the user's notifications (Round 4 — the
    /// per-notification dismiss). Returns true when a row was deleted;
    /// false when the notification doesn't exist OR belongs to a
    /// different user (the caller treats both identically — see the
    /// anti-enumeration note on <see cref="GetByIdForUserAsync"/>).
    /// </summary>
    Task<bool> DeleteForUserAsync(
        Guid notificationId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of a user's UNREAD notifications (ReadAtUtc is
    /// null). Used by the bell-icon badge in the desktop top bar and the
    /// mobile header. Designed to be a fast COUNT(*) with a filtered index
    /// on (UserId, ReadAtUtc) — single-row index seek.
    /// </summary>
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a single notification by Id. Returns null if not found OR if
    /// the notification does not belong to <paramref name="userId"/> — the
    /// caller never sees another user's notification via this method.
    /// </summary>
    Task<Notification?> GetByIdForUserAsync(
        Guid notificationId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks ALL of a user's unread notifications as read in a single
    /// UPDATE statement. Returns the number of rows affected. Idempotent
    /// (already-read rows are untouched).
    /// </summary>
    Task<int> MarkAllAsReadAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a new notification. Used by sale-lifecycle event handlers.
    /// </summary>
    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Defensive: returns true if a notification with the same
    /// (userId, saleId, kind) tuple already exists. Used by event handlers
    /// for the idempotency short-circuit BEFORE attempting an INSERT
    /// (the unique index catches the race anyway, but the short-circuit
    /// avoids a wasted round-trip + retry).
    /// </summary>
    Task<bool> ExistsAsync(
        Guid userId,
        Guid saleId,
        NotificationKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// HARD-DELETES every notification row whose <see cref="Notification.CreatedAtUtc"/>
    /// is older than <paramref name="olderThanUtc"/>. Used by the
    /// <c>NotificationRetentionCleanupHostedService</c> to enforce the
    /// 60-day retention policy — keeps the <c>Notifications</c> table
    /// bounded over multi-year operation (otherwise it grows unbounded
    /// because there's no per-user "delete old notifications" UI flow).
    /// </summary>
    /// <remarks>
    /// <b>WHY A REPOSITORY METHOD (not a raw SQL DELETE in the hosted
    /// service)</b>: keeps the cleanup logic persistence-agnostic at the
    /// Application layer. The Infrastructure EF Core implementation uses
    /// <c>ExecuteDeleteAsync</c> (single DELETE statement, no load-then-
    /// remove round trip).
    /// <para>
    /// <b>READ + UNREAD, ALL USERS</b>: the cleanup is system-wide. A
    /// notification's retention is based purely on age — the user might
    /// have meant to come back to a 2-month-old unread notification, but
    /// the trade-off is bounded storage vs. infinite backlog. The
    /// <c>Notifications</c> page already shows newest-first paginated,
    /// so users see recent activity first; 60-day retention covers any
    /// realistic "scroll back to find that notification from last month"
    /// flow.
    /// </para>
    /// <para>
    /// <b>BROADCAST NOTIFICATIONS ARE SEPARATE</b>: the
    /// <c>BroadcastNotifications</c> audit table is cleaned up by its
    /// own method on <c>IBroadcastNotificationRepository</c>
    /// (<c>DeleteOlderThanAsync</c>) — broadcast audit rows are kept
    /// around longer (or indefinitely, depending on policy) because they
    /// are audit records, not inbox items.
    /// </para>
    /// </remarks>
    /// <param name="olderThanUtc">
    /// The UTC cutoff. Any notification whose <c>CreatedAtUtc</c> is
    /// STRICTLY LESS THAN this value is deleted. Pass
    /// <c>DateTime.UtcNow - TimeSpan.FromDays(60)</c> for the standard
    /// 60-day retention policy.
    /// </param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteOlderThanAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default);
}

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
    /// the unread-only segmented view in the UI).
    /// </summary>
    Task<PaginatedResult<Notification>> GetPaginatedForUserAsync(
        Guid userId,
        int pageNumber,
        int pageSize,
        bool unreadOnly,
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
}

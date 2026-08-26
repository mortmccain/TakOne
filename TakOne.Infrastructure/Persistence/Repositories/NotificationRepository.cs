using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="INotificationRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>TRACKING POLICY</b>:
/// <list type="bullet">
///   <item><see cref="GetPaginatedForUserAsync"/> and <see cref="GetUnreadCountAsync"/>
///         use <c>AsNoTracking</c> — these are read-only UI-list paths; no
///         caller mutates the returned entities.</item>
///   <item><see cref="GetByIdForUserAsync"/> returns a TRACKED entity —
///         it is the load-for-mutation path used by
///         <c>MarkNotificationAsReadCommandHandler</c>, which calls
///         <c>notification.MarkAsRead()</c> and relies on the change
///         tracker to persist the mutation on the next
///         <c>SaveChangesAsync</c>. Without tracking, the mutation would
///         be a silent no-op (no UPDATE generated).</item>
/// </list>
/// </para>
/// <para>
/// <b>SCOPE GUARD</b>: every read method takes <c>userId</c> as a
/// parameter and applies <c>WHERE UserId = userId</c> in the SQL — the
/// caller cannot snoop another user's notifications via this repo.
/// <c>GetByIdForUserAsync</c> applies BOTH the Id AND the UserId filter
/// so a caller passing someone else's notificationId gets null.
/// </para>
/// </remarks>
public sealed class NotificationRepository : INotificationRepository
{
    private readonly ApplicationDbContext _db;

    public NotificationRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<PaginatedResult<Notification>> GetPaginatedForUserAsync(
        Guid userId,
        int pageNumber,
        int pageSize,
        bool unreadOnly,
        CancellationToken cancellationToken = default)
    {
        // Build the base query — always scoped to userId.
        var query = _db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId);

        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAtUtc == null);
        }

        // TotalCount via COUNT(*) — the (UserId, CreatedAtUtc) index makes
        // this a fast index scan.
        var totalCount = await query.CountAsync(cancellationToken);

        // Paginate + newest-first. The (UserId, CreatedAtUtc) index
        // supports the ORDER BY as an index seek + reverse iteration.
        var items = await query
            .OrderByDescending(n => n.CreatedAtUtc)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<Notification>(items, totalCount, pageNumber, pageSize);
    }

    /// <inheritdoc />
    public async Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Hot path — uses the filtered index
        // IX_Notifications_UserId_ReadAtUtc_Unread (WHERE ReadAtUtc IS NULL).
        // COUNT(*) on a filtered index is a tiny index seek.
        return await _db.Notifications
            .AsNoTracking()
            .CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Notification?> GetByIdForUserAsync(
        Guid notificationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Both filters — even if the caller passes someone else's
        // notificationId, the WHERE UserId = @u clause means null is
        // returned. Anti-CSRF: no cross-user reads.
        return await _db.Notifications
            .FirstOrDefaultAsync(
                n => n.Id == notificationId && n.UserId == userId,
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> MarkAllAsReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Single UPDATE statement — no per-row round-trip.
        // SET ReadAtUtc = SYSUTCDATETIME() WHERE UserId = @u AND ReadAtUtc IS NULL
        //
        // We use ExecuteUpdateAsync (EF Core 7+) which generates a single
        // UPDATE statement (vs. loading rows into memory + mutating +
        // SaveChanges which would be N UPDATEs or 1 batch UPDATE).
        var nowUtc = DateTime.UtcNow;
        var affected = await _db.Notifications
            .Where(n => n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(
                setter => setter.SetProperty(n => n.ReadAtUtc, nowUtc),
                cancellationToken);

        return affected;
    }

    /// <inheritdoc />
    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        await _db.Notifications.AddAsync(notification, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(
        Guid userId,
        Guid saleId,
        NotificationKind kind,
        CancellationToken cancellationToken = default)
    {
        // Single EXISTS round-trip — uses the unique index
        // UX_Notifications_UserId_SaleId_Kind (filtered to SaleId IS NOT NULL).
        return await _db.Notifications
            .AsNoTracking()
            .AnyAsync(
                n => n.UserId == userId && n.SaleId == saleId && n.Kind == kind,
                cancellationToken);
    }
}

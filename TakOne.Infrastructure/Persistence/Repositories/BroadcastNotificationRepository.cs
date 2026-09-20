using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IBroadcastNotificationRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>TRACKING POLICY</b>: <see cref="GetPaginatedAsync"/> uses
/// <c>AsNoTracking</c> — pure read path, no caller mutates the returned
/// audit rows (broadcasts are immutable; there is no <c>UpdateAsync</c>
/// method on the interface).
/// <see cref="AddAsync"/> is a write path — the entity is tracked until
/// Wolverine's AutoApplyTransactions calls SaveChangesAsync, which
/// generates the INSERT.
/// </para>
/// <para>
/// <b>NO PER-USER SCOPE GUARD</b>: this repo does NOT filter by user Id.
/// A <c>BroadcastNotification</c> is a system-level audit record. The
/// handler (<c>SendBroadcastNotificationCommandHandler</c> /
/// <c>EmitAppUpdateBroadcastCommandHandler</c>) is gated to Admin role
/// via the <c>[RequireRoles(Admin)]</c> attribute (or is the trusted
/// in-process hosted service for app-update), so only trusted callers
/// reach these methods.
/// </para>
/// </remarks>
public sealed class BroadcastNotificationRepository : IBroadcastNotificationRepository
{
    private readonly ApplicationDbContext _db;

    public BroadcastNotificationRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task AddAsync(
        BroadcastNotification broadcast,
        CancellationToken cancellationToken = default)
    {
        await _db.BroadcastNotifications.AddAsync(broadcast, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BroadcastNotification?> GetByTitleAndKindAsync(
        string title,
        NotificationKind kind,
        DateTime? sentAfterUtc = null,
        CancellationToken cancellationToken = default)
    {
        // AsNoTracking: pure read path for the idempotency dedup. The
        // handler does NOT mutate the returned entity; it only reads
        // RecipientCount to return as the success value. No tracking
        // means no accidental UPDATE when the surrounding SaveChanges
        // runs.
        //
        // Newest-first (OrderByDescending(SentAtUtc)): if a duplicate
        // somehow already exists (e.g. a prior redelivery that slipped
        // through before this dedup was added), return the MOST RECENT
        // one so the reported RecipientCount reflects the latest fanout.
        //
        // TIME-WINDOWED DEDUP (sentAfterUtc): when non-null, restricts
        // the lookup to broadcasts sent strictly AFTER this UTC cutoff.
        // Used by the app-update handler to avoid deduping a NEW deploy's
        // broadcast against an OLD broadcast that happens to share the
        // same constant title ("TakOne updated"). See the interface XML
        // doc for the full rationale. When null, no time filter is
        // applied (the admin-broadcast handler path — original behavior).
        var query = _db.BroadcastNotifications
            .AsNoTracking()
            .Where(b => b.Title == title && b.FanoutKind == kind);

        if (sentAfterUtc.HasValue)
        {
            query = query.Where(b => b.SentAtUtc > sentAfterUtc.Value);
        }

        return await query
            .OrderByDescending(b => b.SentAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PaginatedResult<BroadcastNotification>> GetPaginatedAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // Base query — no user-Id filter (see class doc). AsNoTracking
        // because this is a pure read path (the admin audit page just
        // renders the rows; no caller mutates them).
        var query = _db.BroadcastNotifications
            .AsNoTracking();

        // TotalCount — the SentAtUtc index makes this a fast index scan.
        var totalCount = await query.CountAsync(cancellationToken);

        // Paginate + newest-first. The SentAtUtc index supports the
        // ORDER BY as an index seek + reverse iteration.
        var items = await query
            .OrderByDescending(b => b.SentAtUtc)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<BroadcastNotification>(items, totalCount, pageNumber, pageSize);
    }

    /// <inheritdoc />
    public async Task<int> DeleteOlderThanAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default)
    {
        // Single DELETE statement. Same ExecuteDeleteAsync pattern as
        // NotificationRepository.DeleteOlderThanAsync — no load-into-
        // memory, no per-row round-trip, no change tracker. The
        // SentAtUtc index doesn't directly index `<` range scans but
        // the audit table is bounded by the 60-day retention window so
        // this is fast.
        var affected = await _db.BroadcastNotifications
            .Where(b => b.SentAtUtc < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken);

        return affected;
    }
}

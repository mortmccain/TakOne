using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Entities;
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
}

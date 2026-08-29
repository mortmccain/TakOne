using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;

namespace TakOne.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="INotificationPreferenceRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>TRACKING POLICY</b>:
/// <list type="bullet">
///   <item><see cref="IsMutedAsync"/>, <see cref="GetAllForUserAsync"/>, and
///         <see cref="GetMutedUserIdsAsync"/> use <c>AsNoTracking</c> —
///         read-only suppression/settings-load paths; nobody mutates the
///         returned entities.</item>
///   <item><see cref="GetForUserAsync"/> returns a TRACKED entity — it is
///         the load-for-mutation path used by
///         <c>SetNotificationMutedCommandHandler</c>, which calls
///         <c>Mute()/Unmute()</c> and relies on the change tracker (+
///         Wolverine's transactional <c>SaveChangesAsync</c>) to persist.
///         Without tracking the toggle would be a silent no-op.</item>
/// </list>
/// </para>
/// <para>
/// <b>SCOPE GUARD</b>: every user-scoped read applies
/// <c>WHERE UserId = userId</c> in SQL — a caller cannot read (or flip)
/// another user's preferences through this repo.
/// </para>
/// </remarks>
public sealed class NotificationPreferenceRepository : INotificationPreferenceRepository
{
    private readonly ApplicationDbContext _db;

    public NotificationPreferenceRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Single index seek on <c>UX_NotificationPreferences_UserId_Kind</c>.
    /// Called by every NotifyOn* event handler before creating a
    /// notification row — the suppression hot path — so it must stay a
    /// single round-trip with no materialization beyond the one flag.
    /// </remarks>
    public async Task<bool> IsMutedAsync(
        Guid userId,
        NotificationKind kind,
        CancellationToken cancellationToken = default)
    {
        return await _db.NotificationPreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId && p.Kind == kind)
            .Select(p => p.IsMuted)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationPreference>> GetAllForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        // Sparse table: typically 0–7 rows per user (one per muted kind at
        // most). No paging needed — a full range scan of the user's
        // unique-index partition.
        return await _db.NotificationPreferences
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<NotificationPreference?> GetForUserAsync(
        Guid userId,
        NotificationKind kind,
        CancellationToken cancellationToken = default)
    {
        // TRACKED — the upsert mutation path (see class remarks).
        return await _db.NotificationPreferences
            .FirstOrDefaultAsync(
                p => p.UserId == userId && p.Kind == kind,
                cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The unique index is (UserId, Kind) — kind-first can't seek, so this
    /// is a residual scan over a tiny sparse table. Called ONCE per
    /// broadcast fanout (rare), never per recipient.
    /// </remarks>
    public async Task<IReadOnlySet<Guid>> GetMutedUserIdsAsync(
        NotificationKind kind,
        CancellationToken cancellationToken = default)
    {
        var mutedIds = await _db.NotificationPreferences
            .AsNoTracking()
            .Where(p => p.Kind == kind && p.IsMuted)
            .Select(p => p.UserId)
            .ToListAsync(cancellationToken);

        return mutedIds.ToHashSet();
    }

    /// <inheritdoc />
    public async Task AddAsync(
        NotificationPreference preference,
        CancellationToken cancellationToken = default)
    {
        await _db.NotificationPreferences.AddAsync(preference, cancellationToken);
    }
}

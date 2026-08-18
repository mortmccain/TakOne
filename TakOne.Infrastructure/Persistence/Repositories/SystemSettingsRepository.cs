using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Common.Entities;
using TakOne.Infrastructure.Persistence;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="ISystemSettingsRepository"/>.
///
/// SINGLETON-ROW SEMANTICS:
///   The SystemSettings table contains exactly one row, identified by
///   <see cref="SystemSettings.SingletonId"/> (Guid.Empty). On first read
///   (e.g. fresh install), <see cref="GetOrCreateAsync"/> detects the row
///   is missing and lazily creates it with default values
///   (<c>LimitMode = CountOnly</c>) in the same transaction.
///
/// TRACKING:
///   <see cref="GetOrCreateAsync"/> returns a TRACKED entity — the caller
///   (SetSystemLimitModeCommandHandler) mutates it via
///   <c>SystemSettings.UpdateLimitMode(newMode)</c>, then calls
///   <see cref="UpdateAsync"/> which just calls SaveChanges through
///   <c>IUnitOfWork</c>. The tracking is what lets EF Core generate a
///   clean UPDATE without needing an explicit <c>Update(entity)</c> call.
///
/// CONCURRENCY:
///   There is no optimistic-concurrency token on SystemSettings. The row
///   is updated rarely (admins toggle the mode maybe once a quarter), and
///   the last-writer-wins semantics are acceptable — the cached value is
///   invalidated after every write, so the next read picks up the new
///   value within a few milliseconds.
///
///   If two admins happen to change the mode at the exact same instant,
///   one of them wins. The other's change is silently overwritten. This
///   is documented as accepted behaviour in the design notes (Step 9's
///   error catalog doesn't surface this to the user — it's a non-issue).
/// </summary>
public sealed class SystemSettingsRepository : ISystemSettingsRepository
{
    private readonly ApplicationDbContext _db;

    public SystemSettingsRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<SystemSettings> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Single-row lookup by the constant SingletonId. FirstOrDefaultAsync
        // (not FindAsync) because FindAsync would also consult the change
        // tracker, which is fine — but we want a single round-trip even on
        // a cold DbContext. FirstOrDefaultAsync always hits the DB.
        //
        // We don't use SingleOrDefaultAsync because the unique index on Id
        // already guarantees at most one row — and if the index is somehow
        // missing (defensive: misconfigured DB), we'd rather take the first
        // row and log the anomaly than throw and break the request.
        // ------------------------------------------------------------------
        var settings = await _db.SystemSettings
            .FirstOrDefaultAsync(s => s.Id == SystemSettings.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        // ------------------------------------------------------------------
        // Lazy create — fresh install, the singleton row doesn't exist yet.
        // Create it with default values and SaveChanges immediately so it's
        // persisted before we return. We do NOT defer the SaveChanges to
        // the caller's UoW because:
        //   - If we did, and the caller never calls SaveChanges (e.g. an
        //     exception is thrown upstream), the row would never be created
        //     and every subsequent read would try to recreate it.
        //   - The row creation is idempotent — if two concurrent requests
        //     both try to create it, the unique index on Id rejects the
        //     second INSERT, and the next read picks up the first row.
        //
        // We catch the DbUpdateException from any constraint violation
        // (unique-index OR CHECK-constraint) by re-querying with
        // FirstOrDefaultAsync (NOT FirstAsync — that throws "Sequence
        // contains no elements" if the row still isn't there, which is
        // exactly the runtime bug reported in the Step 12-a logs).
        // If the re-query STILL returns null, we throw a clear, actionable
        // InvalidOperationException instead of letting the caller
        // cascade-fail with a less informative error.
        // ------------------------------------------------------------------
        settings = SystemSettings.CreateDefault();
        _db.SystemSettings.Add(settings);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // ------------------------------------------------------------------
            // Concurrent create OR a CHECK-constraint anomaly — another
            // request may have beat us to it, OR (more rarely) something
            // about the INSERT violated the singleton CHECK constraint
            // (e.g. the row already exists with a slightly different
            // EF-tracked state). Detach our local tracked copy first so
            // we don't pollute the change tracker with a duplicate entity,
            // then re-query.
            // ------------------------------------------------------------------
            var localEntry = _db.ChangeTracker
                .Entries<SystemSettings>()
                .FirstOrDefault(e => e.Entity.Id == SystemSettings.SingletonId);
            localEntry?.State = EntityState.Detached;

            // Re-query with FirstOrDefaultAsync (NOT FirstAsync — the row
            // may legitimately not exist yet if the concurrent INSERT was
            // rolled back, or if the CHECK constraint rejected our INSERT
            // for a reason we don't know about). If the row IS there now,
            // we use it. If it's STILL null, we throw a clear error — the
            // caller (typically SystemSettingsService → cache factory)
            // would otherwise propagate a less-informative "Sequence
            // contains no elements" exception up the call stack.
            settings = await _db.SystemSettings
                .FirstOrDefaultAsync(s => s.Id == SystemSettings.SingletonId, cancellationToken);

            if (settings is null)
            {
                throw new InvalidOperationException(
                    "SystemSettings singleton row could not be loaded after INSERT failed. " +
                    "The CHECK constraint CK_SystemSettings_Id_IsSingleton requires " +
                    "Id = '00000000-0000-0000-0000-000000000000' — verify the row exists " +
                    "in the SystemSettings table and matches that constraint, or re-run " +
                    "the EF Core migration to seed the singleton row.");
            }
        }

        return settings;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(SystemSettings settings, CancellationToken cancellationToken = default)
    {
        // The entity is already tracked (loaded via GetOrCreateAsync).
        // SaveChanges generates the UPDATE automatically — no need to
        // call _db.SystemSettings.Update(settings). In fact, calling
        // Update on an already-tracked entity is a no-op (it just sets
        // the entity state to Modified, which it already is).
        //
        // We DO call SaveChangesAsync here (not defer to IUnitOfWork)
        // because the ISystemSettingsService needs to know the write
        // committed before it can safely invalidate the cache — otherwise
        // a concurrent reader could re-populate the cache with the OLD
        // value after the invalidation but before the SaveChanges.
        //
        // The transaction is the same one the IUnitOfWork will commit,
        // because ApplicationDbContext is scoped and shares one
        // connection per request.
        await _db.SaveChangesAsync(cancellationToken);
    }
}
using TakOne.Domain.Common.Entities;
using TakOne.Domain.Common.Enums;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Cached read wrapper around <see cref="ISystemSettingsRepository"/>.
///
/// WHY THIS EXISTS:
///   The system's <c>LimitMode</c> is read on every purchase-limit check
///   (every cart mutation, every submit). Hitting the DB on every check
///   would be wasteful — the value changes rarely (admins toggle the
///   mode maybe once a quarter). <c>IMemoryCache</c> bridges the gap:
///     - First read: hit DB via <see cref="ISystemSettingsRepository.GetOrCreateAsync"/>,
///       store in cache.
///     - Subsequent reads: hit cache (microsecond cost, in-process).
///     - Admin update via <c>SetSystemLimitModeCommandHandler</c>:
///       write to DB, then call <see cref="InvalidateCacheAsync"/> so
///       the next read re-loads from DB.
///
///   In steady state, zero DB hits for the settings check.
///
/// WHY NOT JUST USE IOptionsMonitor&lt;T&gt;:
///   <c>IOptionsMonitor&lt;T&gt;</c> is designed for the
///   <c>appsettings.json</c> + file-change-notification pattern, not
///   for runtime DB-driven settings. We could implement a custom
///   <c>IOptionsChangeTokenSource&lt;T&gt;</c> backed by the DB, but
///   that's complexity we don't need — the cache is a single value,
///   not a hierarchy of options. <c>IMemoryCache</c> is simpler and
///   correct.
///
/// THREAD-SAFETY:
///   <c>IMemoryCache</c> is thread-safe. Multiple concurrent reads
///   return the same cached value. A concurrent write + invalidate
///   sequence may briefly return the old value on a thread that read
///   the cache between the write and the invalidation — this is
///   acceptable (the next read after invalidation will return the new
///   value, and limit-mode switches don't need to be transactionally
///   visible across all in-flight requests).
/// </summary>
public interface ISystemSettingsService
{
    /// <summary>
    /// Returns the current system-wide limit mode. Hot path — reads
    /// from in-process cache; falls back to DB on cache miss.
    /// </summary>
    Task<LimitMode> GetLimitModeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the full SystemSettings entity (including UpdatedAt for
    /// audit display). Used by the Manage Groups page's "current mode"
    /// indicator. Reads from cache if populated.
    /// </summary>
    Task<SystemSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates the in-process cache entry. Called by
    /// <c>SetSystemLimitModeCommandHandler</c> AFTER successfully
    /// persisting a mode change. The next call to
    /// <see cref="GetLimitModeAsync"/> will re-load from DB.
    ///
    /// This method does NOT need to be async (IMemoryCache's
    /// <c>Remove</c> is synchronous), but the signature is async to
    /// match the convention and to allow a future implementation to
    /// invalidate a distributed cache.
    /// </summary>
    Task InvalidateCacheAsync(CancellationToken cancellationToken = default);
}
using TakOne.Domain.Common.Entities;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Repository for the singleton <see cref="SystemSettings"/> entity.
///
/// This is the LOW-LEVEL repository — it reads/writes the database row
/// directly. Application code should NOT use this directly; use
/// <see cref="ISystemSettingsService"/> instead, which wraps this
/// repository with an in-process <c>IMemoryCache</c> so the hot-path
/// purchase-limit checks don't hit the DB on every request.
///
/// The cache invalidation is the application service's responsibility —
/// this repository is unaware of the cache. When
/// <c>SetSystemLimitModeCommandHandler</c> calls
/// <see cref="UpdateAsync"/>, it ALSO calls
/// <c>ISystemSettingsService.InvalidateCacheAsync</c> so the next read
/// picks up the new value.
/// </summary>
public interface ISystemSettingsRepository
{
    /// <summary>
    /// Returns the singleton SystemSettings row. If the row doesn't exist
    /// (e.g. fresh install, first read), lazily creates it with default
    /// values (<c>LimitMode = CountOnly</c>) and persists it. The lazy
    /// create is wrapped in the repository (not the service) so the cache
    /// never holds a null.
    /// </summary>
    Task<SystemSettings> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists changes to the singleton row. The caller must have loaded
    /// the entity via <see cref="GetOrCreateAsync"/> first (the entity is
    /// tracked by the DbContext for the duration of the request).
    /// </summary>
    Task UpdateAsync(SystemSettings settings, CancellationToken cancellationToken = default);
}
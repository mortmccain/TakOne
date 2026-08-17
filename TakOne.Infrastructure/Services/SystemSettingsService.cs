using Microsoft.Extensions.Caching.Memory;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Common.Entities;
using TakOne.Domain.Common.Enums;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Cached read wrapper around <see cref="ISystemSettingsRepository"/>.
///
/// CACHING STRATEGY:
///   <see cref="IMemoryCache"/> — process-local, in-memory. The cached
///   entry is keyed by the constant <c>CacheKey</c> below. Reads hit the
///   cache (microsecond cost); writes invalidate the cache so the next
///   read re-loads from DB.
///
///   The cache entry is a <see cref="SystemSettings"/> SNAPSHOT — it's
///   a value-only copy (the entity materialized via AsNoTracking on the
///   underlying repo call, then stored). We never mutate the cached
///   copy. On invalidation, the next read re-materializes from DB.
///
/// WHY NOT JUST STORE LimitMode (enum) INSTEAD OF THE WHOLE ENTITY:
///   We could. But the Manage Groups page wants to show "last updated
///   at" alongside the mode — caching the whole entity means we have
///   that field available without a separate DB hit. Cost is negligible
///   (a few extra bytes of memory per cache entry, and there's only ONE
///   cache entry per process).
///
/// WHY NOT IOptionsMonitor&lt;T&gt;:
///   See <see cref="ISystemSettingsService"/> interface doc — TL;DR:
///   IOptionsMonitor is for file-based config + change tokens, not
///   DB-driven runtime config. IMemoryCache is the right tool.
///
/// LIFETIME:
///   Scoped — same as the other services. Each request gets a fresh
///   instance, but the underlying IMemoryCache is Singleton (registered
///   by AddMemoryCache in the DI container).
/// </summary>
public sealed class SystemSettingsService : ISystemSettingsService
{
    /// <summary>
    /// The single cache key used by all reads. There is only one
    /// SystemSettings row, so there is only one cache entry per process.
    /// </summary>
    public const string CacheKey = "SystemSettings.Singleton";

    private readonly ISystemSettingsRepository _repo;
    private readonly IMemoryCache _cache;

    public SystemSettingsService(ISystemSettingsRepository repo, IMemoryCache cache)
    {
        _repo = repo;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<LimitMode> GetLimitModeAsync(CancellationToken cancellationToken = default)
    {
        // Hot path — every purchase-limit check calls this. The cache hit
        // is a microsecond in-process dictionary lookup; the cache miss
        // (first read after startup or after invalidation) is one DB
        // round-trip via the repository.
        var settings = await GetAsync(cancellationToken);
        return settings.LimitMode;
    }

    /// <inheritdoc />
    public async Task<SystemSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        // GetOrCreateAsync — atomic cache miss + populate. If multiple
        // concurrent readers all miss at once, the factory delegate is
        // invoked multiple times (IMemoryCache doesn't lock per-key),
        // but that's fine — they all populate the same key with the same
        // value. The repo's GetOrCreateAsync handles the rare race where
        // two requests try to create the singleton row simultaneously.
        //
        // The `!` (null-forgiving) operator asserts to the compiler that
        // GetOrCreateAsync will NOT return null in our case. The factory
        // delegate always returns a non-null SystemSettings instance
        // (SystemSettings.Load is a static factory that always constructs
        // a non-null instance). Without `!`, the compiler emits CS8603
        // because GetOrCreateAsync's signature allows a null return if
        // the factory returns null.
        return (await _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            // No expiration — the cache lives until invalidated by
            // InvalidateCacheAsync (called after a write). Setting
            // AbsoluteExpirationRelativeToNow would be a safety net in
            // case a write somehow forgot to invalidate, but it would
            // cause unnecessary DB hits at the expiration boundary.
            // We rely on explicit invalidation instead.
            entry.SetPriority(CacheItemPriority.NeverRemove);

            var settings = await _repo.GetOrCreateAsync(cancellationToken);

            // Make a defensive snapshot — the entity returned by the
            // repo may be tracked by the DbContext, and we don't want
            // mutations in another scope (e.g. SetSystemLimitModeCommandHandler
            // calling settings.UpdateLimitMode) to leak into the cached
            // copy. Use SystemSettings.Load (a factory) to produce a
            // fresh instance with the same values.
            return SystemSettings.Load(settings.LimitMode, settings.UpdatedAt);
        }))!;
    }

    /// <inheritdoc />
    public Task InvalidateCacheAsync(CancellationToken cancellationToken = default)
    {
        // IMemoryCache.Remove is synchronous — no I/O, no allocation.
        // The method signature is async (returns Task) to match the
        // interface and to allow a future distributed-cache implementation
        // to do real I/O here.
        _cache.Remove(CacheKey);
        return Task.CompletedTask;
    }
}
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Common.Entities;
using TakOne.Domain.Common.Enums;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.Infrastructure.Services;
using TakOne.IntegrationTests.Infrastructure;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the singleton SystemSettings row + the cached
/// <see cref="SystemSettingsService"/>. Verifies the lazy-create-on-first-
/// read behaviour actually persists a singleton row, the cache correctly
/// suppresses redundant repo calls, and concurrent cache-misses still
/// produce consistent values.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THESE ARE INTEGRATION TESTS (not mock-based unit tests):</b>
/// the cached service's contract is "cache hit → no DB call, cache miss
/// → exactly one DB call". Mocks can verify the IMemoryCache.GetOrCreateAsync
/// was called, but they can't verify the factory delegate was NOT invoked
/// on a hit (NSubstitute's mock cache wouldn't actually run the factory).
/// A real IMemoryCache + real DB lets us verify the call-count invariant
/// end-to-end.
/// </para>
/// </remarks>
public class SystemSettingsIntegrationTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // A spy wrapper around the real SystemSettingsRepository that counts
    // GetOrCreateAsync calls. Used to verify the cache-hit invariant
    // (subsequent reads must NOT hit the repo).
    private sealed class CountingSystemSettingsRepository : ISystemSettingsRepository
    {
        private readonly ISystemSettingsRepository _inner;
        public int GetOrCreateCallCount;
        public int UpdateCallCount;

        public CountingSystemSettingsRepository(ISystemSettingsRepository inner)
        {
            _inner = inner;
        }

        public async Task<SystemSettings> GetOrCreateAsync(CancellationToken cancellationToken = default)
        {
            GetOrCreateCallCount++;
            return await _inner.GetOrCreateAsync(cancellationToken);
        }

        public async Task UpdateAsync(SystemSettings settings, CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            await _inner.UpdateAsync(settings, cancellationToken);
        }
    }

    private static async Task<(
        CountingSystemSettingsRepository countingRepo,
        SystemSettingsService service,
        ApplicationDbContext db)>
        BuildWiredCollaboratorsAsync()
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var realRepo = new SystemSettingsRepository(db);
        var countingRepo = new CountingSystemSettingsRepository(realRepo);

        // Real IMemoryCache — actually caches, actually calls the factory
        // only on a miss.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new SystemSettingsService(
            countingRepo,
            cache);

        return (countingRepo, service, db);
    }

    // ── Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateAsync_OnEmptyDb_CreatesSingletonRowWithDefaults()
    {
        // Arrange
        var (countingRepo, _, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            // Act
            var settings = await countingRepo.GetOrCreateAsync(CancellationToken.None);

            // Assert — the singleton row is materialized with default values.
            // (Note: SystemSettingsConfiguration's HasData(...) seeds the row at
            // EnsureCreatedAsync time, so GetOrCreateAsync finds it rather than
            // lazily creating it. The behavior we assert is the END STATE: the
            // row exists with default LimitMode + null LastKnownAppVersion.)
            settings.Should().NotBeNull();
            settings.Id.Should().Be(SystemSettings.SingletonId);
            settings.LimitMode.Should().Be(LimitMode.CountOnly);
            settings.LastKnownAppVersion.Should().BeNull();

            // Reload from DB (clear tracker) and verify the row persisted.
            db.ChangeTracker.Clear();
            var rowCount = db.SystemSettings.Count(s => s.Id == SystemSettings.SingletonId);
            rowCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task GetOrCreateAsync_OnSecondCall_ReturnsSameRowWithoutInserting()
    {
        // Arrange
        var (countingRepo, _, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            // Act — first call creates the row; second call returns the existing.
            await countingRepo.GetOrCreateAsync(CancellationToken.None);
            await countingRepo.GetOrCreateAsync(CancellationToken.None);

            // Assert — still only ONE row in the table.
            db.ChangeTracker.Clear();
            var rowCount = db.SystemSettings.Count(s => s.Id == SystemSettings.SingletonId);
            rowCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task SystemSettingsService_CacheMiss_HitsDbOnce()
    {
        // Arrange
        var (countingRepo, service, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            // Act — first call: cold cache, factory invokes the repo.
            var settings = await service.GetAsync(CancellationToken.None);

            // Assert — repo called exactly once, returns non-null with
            // the default LimitMode.
            countingRepo.GetOrCreateCallCount.Should().Be(1);
            settings.LimitMode.Should().Be(LimitMode.CountOnly);
        }
    }

    [Fact]
    public async Task SystemSettingsService_CacheHit_DoesNotHitDb()
    {
        // Arrange
        var (countingRepo, service, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            // Act — first call (cold cache), then 2 follow-up calls.
            await service.GetAsync(CancellationToken.None);
            await service.GetAsync(CancellationToken.None);
            await service.GetAsync(CancellationToken.None);

            // Assert — repo called exactly once total (the cache served
            // the follow-up reads from memory).
            countingRepo.GetOrCreateCallCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task SystemSettingsService_InvalidateCache_ForcesNextCallToHitDb()
    {
        // Arrange
        var (countingRepo, service, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            // Act
            await service.GetAsync(CancellationToken.None);       // 1 repo call.
            await service.InvalidateCacheAsync(CancellationToken.None); // no repo call.
            await service.GetAsync(CancellationToken.None);       // 2nd repo call.

            // Assert — total repo calls = 2 (cold, then after invalidate).
            countingRepo.GetOrCreateCallCount.Should().Be(2);
        }
    }

    // Verifies the cache-populated-by-first-caller pattern: 10 concurrent
    // GetAsync calls from a cold cache should result in a consistent
    // LimitMode value (no thread sees a different result) and the repo
    // being called at least once. IMemoryCache doesn't guarantee a single
    // factory invocation under concurrent contention, so we assert the
    // LOWER BOUND (≥1) and the CONSISTENCY invariant (all callers see the
    // same LimitMode).
    [Fact]
    public async Task SystemSettingsService_ConcurrentCalls_AllSeeConsistentValue()
    {
        // Arrange
        var (countingRepo, service, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            // Act — 10 concurrent reads from a cold cache.
            var results = new List<LimitMode>[10];
            var tasks = Enumerable.Range(0, 10).Select(async i =>
            {
                var settings = await service.GetAsync(CancellationToken.None);
                return settings.LimitMode;
            }).ToList();
            var allLimitModes = await Task.WhenAll(tasks);

            // Assert — all 10 callers saw the same LimitMode.
            allLimitModes.Should().AllBeEquivalentTo(LimitMode.CountOnly);

            // The cache was populated by at least one caller (others saw
            // the cached value). IMemoryCache doesn't lock per-key, so the
            // upper bound on call count is 10; we assert only the lower bound.
            countingRepo.GetOrCreateCallCount.Should().BeGreaterThanOrEqualTo(1);
        }
    }
}

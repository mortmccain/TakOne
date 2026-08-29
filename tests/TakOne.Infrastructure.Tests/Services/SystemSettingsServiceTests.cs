using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Common.Entities;
using TakOne.Domain.Common.Enums;
using TakOne.Infrastructure.Services;
using Xunit;

namespace TakOne.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SystemSettingsService"/>.
///
/// COVERAGE APPROACH:
///   The service wraps <see cref="ISystemSettingsRepository.GetOrCreateAsync"/>
///   with an <see cref="IMemoryCache"/> layer keyed by the constant
///   <see cref="SystemSettingsService.CacheKey"/>. The cache entry is set
///   to <see cref="CacheItemPriority.NeverRemove"/> so the singleton isn't
///   evicted under memory pressure. Tests cover:
///     • cache miss → calls repo GetOrCreateAsync once
///     • cache hit → does NOT call repo
///     • GetLimitModeAsync returns the cached LimitMode
///     • InvalidateCacheAsync forces the next GetAsync to re-hit the repo
///     • the cache entry's priority is NeverRemove
///     • concurrent GetAsync calls from a cold cache — at least one repo call
///     • null CancellationToken is accepted
///     • CacheKey is the documented constant
/// </summary>
public class SystemSettingsServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    private const string IRR = "IRR";

    // Builds a fresh, real MemoryCache instance. Each test gets its own
    // cache — no cross-test interference.
    private static MemoryCache BuildCache() => new(new MemoryCacheOptions());

    // Builds a real SystemSettings instance with the supplied LimitMode.
    private static SystemSettings BuildSettings(LimitMode mode)
        => SystemSettings.Load(mode, DateTime.UtcNow, "1.0.0-test");

    // ── CacheKey constant ────────────────────────────────────────────

    [Fact]
    public void CacheKey_IsDocumentedSingletonKey()
    {
        // Arrange / Act / Assert
        // The cache key is a public const — production code (the
        // invalidator) and tests both depend on its exact string value.
        SystemSettingsService.CacheKey.Should().Be("SystemSettings.Singleton");
    }

    // ── Cache miss → repo call ───────────────────────────────────────

    [Fact]
    public async Task GetAsync_OnColdCache_CallsRepoGetOrCreateOnce()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var settings = BuildSettings(LimitMode.CountOnly);
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(settings);
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);

        // Act
        var result = await sut.GetAsync(CancellationToken.None);

        // Assert
        result.LimitMode.Should().Be(LimitMode.CountOnly);
        await repo.Received(1).GetOrCreateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLimitModeAsync_OnColdCache_ReturnsRepoLimitMode()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var settings = BuildSettings(LimitMode.Both);
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(settings);
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);

        // Act
        var mode = await sut.GetLimitModeAsync(CancellationToken.None);

        // Assert
        mode.Should().Be(LimitMode.Both);
    }

    // ── Cache hit → no repo call ─────────────────────────────────────

    [Fact]
    public async Task GetAsync_OnCacheHit_DoesNotCallRepo()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var settings = BuildSettings(LimitMode.SalaryOnly);
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(settings);
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);
        // Prime the cache.
        await sut.GetAsync(CancellationToken.None);

        // Act
        await sut.GetAsync(CancellationToken.None);
        await sut.GetAsync(CancellationToken.None);

        // Assert
        // 2 follow-up reads must NOT trigger another repo call.
        await repo.Received(1).GetOrCreateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetLimitModeAsync_OnCacheHit_DoesNotCallRepo()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var settings = BuildSettings(LimitMode.CountOnly);
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(settings);
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);
        await sut.GetLimitModeAsync(CancellationToken.None); // prime

        // Act
        await sut.GetLimitModeAsync(CancellationToken.None);

        // Assert
        await repo.Received(1).GetOrCreateAsync(Arg.Any<CancellationToken>());
    }

    // ── InvalidateCacheAsync forces the next read to hit the repo ────

    [Fact]
    public async Task InvalidateCacheAsync_OnNextGetAsync_RepoIsHitAgain()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var settings1 = BuildSettings(LimitMode.CountOnly);
        var settings2 = BuildSettings(LimitMode.Both); // different mode
        var callCount = 0;
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? settings1 : settings2;
            });
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);

        // Act
        var firstRead = await sut.GetAsync(CancellationToken.None);
        await sut.InvalidateCacheAsync(CancellationToken.None);
        var secondRead = await sut.GetAsync(CancellationToken.None);

        // Assert
        firstRead.LimitMode.Should().Be(LimitMode.CountOnly);
        secondRead.LimitMode.Should().Be(LimitMode.Both);
        await repo.Received(2).GetOrCreateAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateCacheAsync_OnNextGetLimitModeAsync_ReturnsNewValue()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var settings1 = BuildSettings(LimitMode.CountOnly);
        var settings2 = BuildSettings(LimitMode.Both);
        var callCount = 0;
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? settings1 : settings2;
            });
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);

        // Act
        var mode1 = await sut.GetLimitModeAsync(CancellationToken.None);
        await sut.InvalidateCacheAsync(CancellationToken.None);
        var mode2 = await sut.GetLimitModeAsync(CancellationToken.None);

        // Assert
        mode1.Should().Be(LimitMode.CountOnly);
        mode2.Should().Be(LimitMode.Both);
    }

    // ── Cache entry priority is NeverRemove ─────────────────────────

    // The SUT calls entry.SetPriority(CacheItemPriority.NeverRemove)
    // inside the cache factory. We verify this by mocking IMemoryCache
    // AND ICacheEntry with NSubstitute — the SUT's factory is invoked
    // by the GetOrCreateAsync extension with our mock entry, and we
    // assert SetPriority was called on it.
    [Fact]
    public async Task GetAsync_OnColdCache_SetsCacheEntryPriorityToNeverRemove()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var settings = BuildSettings(LimitMode.CountOnly);
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(settings);
        // NSubstitute mock for IMemoryCache: TryGetValue returns false
        // (cache miss), CreateEntry returns a mock ICacheEntry we can
        // assert against.
        var cache = Substitute.For<IMemoryCache>();
        var mockEntry = Substitute.For<ICacheEntry>();
        cache.TryGetValue(Arg.Any<object>(), out Arg.Any<object?>())
            .Returns(false);
        cache.CreateEntry(Arg.Any<object>()).Returns(mockEntry);

        var sut = new SystemSettingsService(repo, cache);

        // Act
        await sut.GetAsync(CancellationToken.None);

        // Assert
        // The captured entry's Priority must be NeverRemove — this is
        // the documented defense against eviction under memory pressure.
        mockEntry.Received(1).SetPriority(CacheItemPriority.NeverRemove);
    }

    // ── Concurrent GetAsync calls from cold cache ────────────────────

    // IMemoryCache doesn't lock per-key on a cache miss — concurrent
    // readers may all run the factory. The SUT's defensive snapshot
    // pattern (SystemSettings.Load) means all of them produce an
    // equivalent value, but the repo MAY be called multiple times.
    // This test asserts that AT LEAST ONE repo call happens (i.e. the
    // factory actually runs); it does NOT pin the upper bound.
    [Fact]
    public async Task GetAsync_OnConcurrentColdCacheCalls_MakesAtLeastOneRepoCall()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var settings = BuildSettings(LimitMode.CountOnly);
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(settings));
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);

        // Act
        // 10 concurrent GetAsync calls on a cold cache.
        await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => sut.GetAsync(CancellationToken.None))));

        // Assert
        // At least one call hit the repo. May be MORE — IMemoryCache
        // doesn't lock per-key, so concurrent cold-cache callers can all
        // miss and all hit the repo before the first one populates the
        // entry (benign cache stampede; every caller gets the same
        // settings). Pinning the count to exactly 1 made this test flaky
        // under thread-pool contention — the assertion now matches the
        // documented contract.
        var repoCallCount = repo.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(ISystemSettingsRepository.GetOrCreateAsync));
        repoCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── GetAsync never returns null ───────────────────────────────────

    [Fact]
    public async Task GetAsync_AlwaysReturnsNonNull()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var settings = BuildSettings(LimitMode.SalaryOnly);
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(settings);
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);

        // Act
        var result = await sut.GetAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeOfType<SystemSettings>();
    }

    // ── Defensive snapshot — the cached SystemSettings is a fresh copy

    // The SUT calls SystemSettings.Load inside the factory to make a
    // DEFENSIVE SNAPSHOT — mutations to the SystemSettings instance
    // held by the repo (e.g. via UpdateLimitMode) must NOT leak into
    // the cached copy. This test mutates the original instance AFTER
    // caching and asserts the cached copy's LimitMode is unchanged.
    [Fact]
    public async Task GetAsync_OnCacheHit_ReturnsSnapshotUnaffectedByRepoMutation()
    {
        // Arrange
        // Build a single tracked settings instance that the repo returns.
        // Use the real UpdateLimitMode method (it bumps UpdatedAt) to
        // simulate a write between two reads.
        var settings = SystemSettings.CreateDefault(); // LimitMode = CountOnly
        var repo = Substitute.For<ISystemSettingsRepository>();
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(settings);
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);

        // Act — read once (populates the cache), then have the repo
        // mutate its tracked instance, then read again from the cache.
        var firstRead = await sut.GetAsync(CancellationToken.None);
        // Simulate a tracked-entity mutation (the handler path that
        // calls settings.UpdateLimitMode then SaveChanges — the EF change
        // tracker sees the in-place mutation).
        settings.UpdateLimitMode(LimitMode.Both);
        var secondRead = await sut.GetAsync(CancellationToken.None);

        // Assert
        // The cached copy reflects the snapshot — the in-place mutation
        // did NOT leak into the cache.
        firstRead.LimitMode.Should().Be(LimitMode.CountOnly);
        secondRead.LimitMode.Should().Be(LimitMode.CountOnly,
            "the cache holds a snapshot, not the live tracked instance");
    }

    // ── Null CancellationToken accepted ──────────────────────────────

    // The methods accept `CancellationToken cancellationToken = default`
    // (which is CancellationToken.None). Passing CancellationToken.None
    // explicitly is the documented contract — we verify no NRE.
    [Fact]
    public async Task GetAsync_WithDefaultCancellationToken_DoesNotThrow()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(BuildSettings(LimitMode.CountOnly));
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);

        // Act
        Func<Task> act = async () => await sut.GetAsync(default);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InvalidateCacheAsync_WithDefaultCancellationToken_DoesNotThrow()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);

        // Act
        Func<Task> act = async () => await sut.InvalidateCacheAsync(default);

        // Assert
        await act.Should().NotThrowAsync();
    }

    // ── CancellationToken forwarded to repo ──────────────────────────

    [Fact]
    public async Task GetAsync_ForwardsCancellationTokenToRepo()
    {
        // Arrange
        var repo = Substitute.For<ISystemSettingsRepository>();
        repo.GetOrCreateAsync(Arg.Any<CancellationToken>())
            .Returns(BuildSettings(LimitMode.CountOnly));
        var cache = BuildCache();
        var sut = new SystemSettingsService(repo, cache);
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await sut.GetAsync(ct);

        // Assert
        await repo.Received(1).GetOrCreateAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

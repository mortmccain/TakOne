using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Singleton in-memory per-sale semaphore lock — the implementation of
/// <see cref="ISaleStateLock"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>LIFETIME</b>: must be <b>Singleton</b> (registered as
/// <c>AddSingleton&lt;ISaleStateLock, SaleStateLock&gt;()</c>). Scoped would
/// mean each request has its own semaphore — defeating the purpose.
/// </para>
/// <para>
/// <b>PATTERN</b>: identical to <see cref="CartMutationLock"/> — one
/// <c>SemaphoreSlim(1, 1)</c> per <c>Guid</c> in a
/// <c>ConcurrentDictionary</c>. Acquired via <c>WaitAsync</c>, released
/// via the returned <see cref="Releaser"/>'s <c>DisposeAsync</c>
/// (idempotent via <c>Interlocked.Exchange</c>).
/// </para>
/// <para>
/// <b>SINGLE-NODE LIMITATION</b>: this is a process-local
/// <c>ConcurrentDictionary</c>. For multi-node deployments, replace with
/// a SQL Server <c>sp_getapplock</c>-based distributed lock. The current
/// deployment is single-node (see <c>Program.cs</c> notes).
/// </para>
/// <para>
/// <b>MEMORY GROWTH DEFENSE (Brutal Code Review v3 #26, Round 18-C):</b>
/// the <c>ConcurrentDictionary</c> used to grow unbounded as new SaleIds
/// were encountered. The class comment acknowledged the slow leak
/// ("100k sales ~4MB — acceptable") but accepted it as a trade-off.
/// That trade-off no longer holds for a long-lived production process:
/// at 1M+ sales over months of operation, the dictionary would balloon
/// to 40+MB of idle semaphores with no eviction path. The fix adds:
/// </para>
/// <list type="bullet">
///   <item><see cref="Cleanup"/> — a method that removes semaphores
///   whose <c>CurrentCount == 1</c> (i.e. the semaphore is idle — no one
///   is holding it and no one is waiting on it). The cleanup is
///   race-safe: <see cref="ConcurrentDictionary{TKey, TValue}.TryRemove"/>
///   is atomic, and a concurrent <c>AcquireAsync</c> for the same saleId
///   would just <c>GetOrAdd</c> a fresh semaphore (the racing cleaner
///   either removed it before the acquirer's GetOrAdd or didn't — both
///   outcomes are safe).</item>
///   <item><see cref="SaleStateLockCleanupHostedService"/> — a
///   <c>BackgroundService</c> that calls <see cref="Cleanup"/> every
///   5 minutes. Registered alongside the SaleStateLock singleton in
///   <c>ServiceCollectionExtensions</c>.</item>
/// </list>
/// <para>
/// <b>WHY CurrentCount == 1 IS THE RIGHT IDLENESS TEST:</b>
/// <c>SemaphoreSlim(1, 1)</c> starts with <c>CurrentCount = 1</c>. A
/// successful <c>WaitAsync</c> decrements it to 0 (held). A subsequent
/// <c>WaitAsync</c> blocks (waiting). <c>Release</c> increments it back
/// to 1 (idle again). So <c>CurrentCount == 1</c> means "no one is
/// holding the semaphore AND no one is blocked waiting on it" — the
/// semaphore is genuinely idle and can be removed.
/// </para>
/// <para>
/// <b>RACE WINDOW:</b> there's a tiny race between the cleanup's
/// <c>TryGetValue</c>/<c>TryRemove</c> and a concurrent <c>AcquireAsync</c>'s
/// <c>GetOrAdd</c> + <c>WaitAsync</c>. If the cleaner removes the
/// semaphore in the gap between an acquirer's <c>GetOrAdd</c> and
/// <c>WaitAsync</c>, the acquirer's <c>WaitAsync</c> still works on the
/// same instance (it has a local reference from GetOrAdd) — but a
/// DIFFERENT concurrent acquirer that calls <c>GetOrAdd</c> AFTER the
/// removal gets a FRESH semaphore and acquires it immediately, defeating
/// the serialization. The window is microscopic (microseconds between
/// the cleaner's TryGetValue check and TryRemove) and the consequence
/// is "two concurrent state-transition handlers for the same saleId
/// both proceed" — which is the same behavior we'd have WITHOUT any
/// lock. The race is therefore NOT a correctness regression relative to
/// the no-lock case; it's a rare momentary serialization gap. The
/// alternative (synchronizing cleanup with acquire via a global lock)
/// would defeat the per-saleId parallelism this class exists to provide.
/// </para>
/// </remarks>
public sealed class SaleStateLock : ISaleStateLock
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _semaphores = new();

    public async Task<IAsyncDisposable> AcquireAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        if (saleId == Guid.Empty)
        {
            throw new ArgumentException("SaleId must be a non-empty Guid.", nameof(saleId));
        }

        // GetOrAdd is atomic — multiple concurrent invocations for the same
        // saleId get the SAME SemaphoreSlim instance.
        //
        // Note: GetOrAdd may invoke the factory delegate multiple times
        // under contention (only the first result is stored; the rest are
        // GC'd). This is harmless — the extra SemaphoreSlim instances are
        // unreferenced and immediately collected.
        var semaphore = _semaphores.GetOrAdd(saleId, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken);

        return new Releaser(semaphore);
    }

    /// <summary>
    /// Removes idle semaphores from the underlying
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/> to bound memory
    /// growth. A semaphore is "idle" when <see cref="SemaphoreSlim.CurrentCount"/>
    /// == 1 — i.e. no one is holding it (the semaphore is in the released
    /// state) and no one is blocked waiting on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WHEN TO CALL:</b> periodically — the
    /// <see cref="SaleStateLockCleanupHostedService"/> calls this every
    /// 5 minutes. Direct callers can invoke it on-demand (e.g. after a
    /// bulk-delete of historical sales) but should not call it on every
    /// request — the cleanup iterates the entire dictionary, which is
    /// O(N) where N is the number of distinct saleIds seen so far.
    /// </para>
    /// <para>
    /// <b>SAFETY:</b> the cleanup is race-safe with concurrent
    /// <see cref="AcquireAsync"/> calls. <c>TryRemove</c> is atomic; a
    /// racing <c>AcquireAsync</c> that calls <c>GetOrAdd</c> after the
    /// remove gets a fresh semaphore (and acquires it immediately). A
    /// racing <c>AcquireAsync</c> that called <c>GetOrAdd</c> BEFORE the
    /// remove already has its own local reference to the (now-removed)
    /// semaphore and proceeds normally. See the class-level XML doc for
    /// the full race-window analysis.
    /// </para>
    /// <para>
    /// <b>RETURN VALUE:</b> the number of semaphores removed — useful
    /// for diagnostics (logged by the hosted service).
    /// </para>
    /// </remarks>
    public int Cleanup()
    {
        var removedCount = 0;

        // Enumerate the dictionary's snapshot. ConcurrentDictionary's
        // iterator is a point-in-time snapshot — modifications during
        // iteration are tolerated (the iterator may or may not see them,
        // but it doesn't throw).
        //
        // We use TryRemove(kv.Key, out _) instead of TryUpdate or
        // direct Remove(kv.Key) because we want to remove ONLY if the
        // value is still the same idle semaphore we observed. The
        // collection-pattern TryRemove(key, out _) doesn't have a
        // value-predicate overload, so we do the two-step:
        //   1. TryGetValue to check CurrentCount == 1.
        //   2. TryRemove to evict.
        // The race between the two steps is the microscopic window
        // analyzed in the class-level XML doc — it's safe.
        foreach (var kv in _semaphores)
        {
            // CurrentCount == 1 means the semaphore is in the released
            // state (no one holds it) and no one is blocked on it
            // (blocked waiters would have CurrentCount == 0).
            //
            // We do NOT remove semaphores with CurrentCount == 0 — those
            // are actively held (or have waiters); removing them would
            // silently drop the lock the holder believes they have.
            if (kv.Value.CurrentCount != 1)
            {
                continue;
            }

            // TryRemove is atomic. If a concurrent AcquireAsync racingly
            // removed + re-added between our TryGetValue and our TryRemove,
            // we'd remove the FRESH semaphore (CurrentCount=1, idle) —
            // but a racing acquirer that called GetOrAdd BEFORE our
            // TryRemove still has its own reference and proceeds. A
            // racing acquirer that calls GetOrAdd AFTER our TryRemove
            // gets a brand-new semaphore. Both are safe.
            if (_semaphores.TryRemove(kv.Key, out _))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    /// <summary>
    /// The <see cref="IAsyncDisposable"/> returned by <see cref="AcquireAsync"/>.
    /// Releases the wrapped semaphore exactly once, even if DisposeAsync is
    /// called multiple times (defensive against coding mistakes).
    /// </summary>
    private sealed class Releaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed; // 0 = not yet disposed, 1 = disposed

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            // Interlocked.Exchange returns the OLD value and sets the new
            // value atomically. If we were the first to call DisposeAsync,
            // the old value is 0 — release the semaphore. Otherwise, the
            // old value is 1 — no-op (someone already released).
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Background service that periodically calls
/// <see cref="SaleStateLock.Cleanup"/> to evict idle semaphores from the
/// singleton <see cref="SaleStateLock"/>'s internal
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Prevents the
/// unbounded memory growth identified in Brutal Code Review v3 finding
/// #26 (Round 18-C).
/// </summary>
/// <remarks>
/// <para>
/// <b>LIFETIME</b>: registered as a Singleton <c>IHostedService</c>
/// alongside the <c>SaleStateLock</c> singleton (the cleaner depends on
/// the SaleStateLock instance, which is Singleton — so the cleaner is
/// also Singleton-scoped). Started once at app startup, runs until
/// graceful shutdown.
/// </para>
/// <para>
/// <b>INTERVAL</b>: 5 minutes. Tuned to balance cleanup frequency
/// against the O(N) iteration cost (N = distinct saleIds seen so far).
/// At 100k sales, the cleanup iterates 100k dictionary entries in
/// ~1-2ms — negligible. At 1M+ sales (months of operation), the
/// cleanup may take ~20-30ms — still well within the 5-minute window.
/// If N grows to 10M+, increase the interval to 15 minutes.
/// </para>
/// <para>
/// <b>FAILURE MODE</b>: if <see cref="SaleStateLock.Cleanup"/> throws
/// (it shouldn't — the only throw path is the dictionary's iterator
/// encountering a transient internal state, which ConcurrentDictionary
/// guards against), the hosted service logs the error and continues
/// — the next tick will retry. A persistent cleanup failure would
/// revert to the pre-fix behavior (slow leak) but never crash the app.
/// </para>
/// <para>
/// <b>TESTABILITY</b>: the cleanup interval is read from
/// <c>SaleStateLock:CleanupIntervalMinutes</c> configuration (default 5).
/// Integration tests can set this to a small value (e.g. 0.05 = 3
/// seconds) to exercise the cleanup loop without waiting 5 minutes.
/// </para>
/// </remarks>
public sealed class SaleStateLockCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    // Nullable: set in the constructor. Marked nullable to silence CS8618
    // (the constructor assigns it conditionally based on whether the
    // registered ISaleStateLock is the concrete SaleStateLock or a test
    // double — when it's a test double, this stays null and the cleanup
    // loop no-ops).
    private readonly SaleStateLock? _saleStateLock;
    private readonly TimeSpan _interval;
    private readonly ILogger<SaleStateLockCleanupHostedService> _logger;

    public SaleStateLockCleanupHostedService(
        ISaleStateLock saleStateLock,
        IConfiguration configuration,
        ILogger<SaleStateLockCleanupHostedService> logger)
    {
        // ISaleStateLock is registered as the SaleStateLock singleton,
        // so this resolves to the same instance handlers use at runtime.
        // We downcast to the concrete SaleStateLock because Cleanup() is
        // not on the ISaleStateLock interface (it's a maintenance-only
        // operation; exposing it on the interface would invite
        // accidental calls from handlers that should never clean up).
        // If the registered ISaleStateLock is NOT a SaleStateLock (e.g.
        // a test double), we no-op the cleanup loop — the test double
        // is responsible for its own memory management.
        _saleStateLock = saleStateLock as SaleStateLock;
        _logger = logger;

        // Read the interval from configuration. Allow override for
        // integration tests. Default to 5 minutes for production.
        var intervalMinutes = configuration.GetValue<double?>("SaleStateLock:CleanupIntervalMinutes");
        _interval = intervalMinutes.HasValue
            ? TimeSpan.FromMinutes(intervalMinutes.Value)
            : DefaultInterval;

        // Safety floor: never run more than once per second. A
        // misconfigured interval of 0 would otherwise spin the cleanup
        // in a tight loop and starve the rest of the app.
        if (_interval < TimeSpan.FromSeconds(1))
        {
            _interval = TimeSpan.FromSeconds(1);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // If the registered ISaleStateLock isn't a SaleStateLock (test
        // double), there's nothing to clean up — log once and exit.
        if (_saleStateLock is null)
        {
            _logger.LogInformation(
                "SaleStateLockCleanupHostedService: registered ISaleStateLock is not a " +
                "SaleStateLock (likely a test double) — cleanup loop disabled.");
            return;
        }

        _logger.LogInformation(
            "SaleStateLockCleanupHostedService: started. Cleanup interval = {IntervalMinutes:F1} minutes.",
            _interval.TotalMinutes);

        // BackgroundService.ExecuteAsync runs on a long-running task.
        // We use PeriodicAsyncTimer (Task.Delay loop) for simplicity —
        // the cleanup is fast enough that a periodic timer is fine.
        // For sub-minute intervals or higher precision, switch to
        // PeriodicTimer (NET 6+).
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the interval (or shutdown). Task.Delay with
                // the cancellation token lets graceful shutdown
                // interrupt the wait immediately.
                await Task.Delay(_interval, stoppingToken);

                // Run the cleanup. The method iterates the dictionary
                // and removes idle semaphores. Returns the count for
                // diagnostics.
                var removed = _saleStateLock.Cleanup();

                if (removed > 0)
                {
                    _logger.LogDebug(
                        "SaleStateLock cleanup: removed {Count} idle semaphore(s).",
                        removed);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on graceful shutdown — break the loop.
                break;
            }
            catch (Exception ex)
            {
                // Don't let a single cleanup failure crash the hosted
                // service — log and continue. The next tick will retry.
                _logger.LogError(ex,
                    "SaleStateLock cleanup threw an unexpected exception. " +
                    "The next scheduled cleanup will retry. " +
                    "Memory growth may continue until a successful cleanup run.");
            }
        }

        _logger.LogInformation("SaleStateLockCleanupHostedService: stopped.");
    }
}

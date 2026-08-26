using System.Collections.Concurrent;
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
/// <b>MEMORY LEAK DEFENSE</b>: the <c>ConcurrentDictionary</c> grows
/// unbounded as new SaleIds are encountered. In a long-running server
/// this could be a slow leak. Mitigation options (in order of complexity):
/// <list type="number">
///   <item>Add a periodic cleanup that removes semaphores with
///       CurrentCount == 1 (no waiters) — but ConcurrentDictionary
///       doesn't expose CurrentCount without TryGetValue. Acceptable
///       trade-off for a single-node deployment with bounded sale volume.</item>
///   <item>Use <c>ConditionalWeakTable&lt;Guid, SemaphoreSlim&gt;</c>
///       instead — but Guid is a value type so this requires a wrapper.</item>
///   <item>Switch to a real distributed lock (Redis redlock, SQL Server
///       sp_getapplock) for multi-node.</item>
/// </list>
/// The current implementation accepts the slow leak — semaphores are
/// tiny (40 bytes each + the dictionary's overhead), and a sale-volume
/// of 100k would consume ~4MB. The container will restart long before
/// this matters.
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
        var semaphore = _semaphores.GetOrAdd(saleId, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken);

        return new Releaser(semaphore);
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

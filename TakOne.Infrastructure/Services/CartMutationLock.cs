using System.Collections.Concurrent;
using TakOne.Application.Common.Interfaces;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Default implementation of <see cref="ICartMutationLock"/>. Uses a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> of
/// <see cref="SemaphoreSlim"/> instances, one per user Id.
///
/// LIFETIME:
///   Registered as <b>Singleton</b> so all requests share the same
///   dictionary. (A Scoped lifetime would create a new dictionary per
///   request, which would defeat the entire purpose — concurrent
///   requests wouldn't see each other's semaphores.)
///
/// MEMORY GROWTH NOTE:
///   The dictionary grows unboundedly with the number of unique users
///   who have ever mutated a cart. For a system with ~10K active users,
///   each <see cref="SemaphoreSlim"/> costs roughly 32 bytes, so the
///   total memory footprint is ~320KB — negligible. If the system ever
///   scales to millions of unique users, an LRU eviction strategy would
///   be needed; for now the simplicity of unconditional retention wins.
///
/// The returned <see cref="IAsyncDisposable"/> is a struct-style releaser
/// that calls <see cref="SemaphoreSlim.Release"/> exactly once on
/// disposal (idempotent via <see cref="Interlocked.Exchange"/>) — safe to
/// dispose multiple times without over-releasing.
/// </summary>
public sealed class CartMutationLock : ICartMutationLock
{
    // ------------------------------------------------------------------
    // Singleton-per-userId semaphore table. ConcurrentDictionary handles
    // the racing first-touch case (two threads calling GetOrAdd for the
    // same user simultaneously). The factory delegate may be invoked more
    // than once for the same key under contention, but only one of the
    // resulting SemaphoreSlim instances is stored — the rest are GC'd.
    // ------------------------------------------------------------------
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _semaphores = new();

    /// <inheritdoc/>
    public async Task<IAsyncDisposable> AcquireAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException(
                "CartMutationLock.AcquireAsync: userId must not be Guid.Empty. " +
                "The caller (a sale-mutating handler) is responsible for ensuring " +
                "the customer's Id is populated before acquiring the lock.",
                nameof(userId));
        }

        var semaphore = _semaphores.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

        // WaitAsync(ct) throws OperationCanceledException if the token
        // fires before the semaphore is acquired. The caller's handler
        // will see the cancellation propagate as a canceled HTTP request
        // — no semaphore is held, so no leak.
        await semaphore.WaitAsync(cancellationToken);

        return new Releaser(semaphore);
    }

    /// <summary>
    /// The <see cref="IAsyncDisposable"/> returned by
    /// <see cref="AcquireAsync"/>. Holds a reference to the acquired
    /// semaphore and releases it exactly once on disposal.
    ///
    /// Implemented as a private sealed class (rather than a record
    /// struct) because <see cref="SemaphoreSlim"/> is a reference type
    /// and we need finalizer-free deterministic release — the caller's
    /// <c>await using</c> drives disposal at scope exit.
    /// </summary>
    private sealed class Releaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        // Idempotent dispose — defensive against accidental double-using
        // (which would otherwise over-release the semaphore and let
        // another waiter sneak through prematurely).
        private int _disposed;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
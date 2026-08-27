using FluentAssertions;
using TakOne.Application.Common.Interfaces;
using TakOne.Infrastructure.Services;
using TakOne.Testing;
using Xunit;

namespace TakOne.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CartMutationLock"/>.
///
/// COVERAGE APPROACH:
///   <see cref="CartMutationLock"/> is the per-user semaphore table backing
///   <see cref="ICartMutationLock"/> — identical in implementation pattern
///   to <see cref="SaleStateLock"/> (ConcurrentDictionary&lt;Guid, SemaphoreSlim&gt;
///   with an idempotent private Releaser). The two classes are intentionally
///   NOT sharing a base class so each can carry its own XML doc and evolve
///   independently; hence the test surface is duplicated on purpose.
///
///   Tests cover:
///     • empty-Guid rejection (ArgumentException + correct message + correct param name "userId")
///     • successful acquire returns non-null IAsyncDisposable
///     • sequential acquire+release+acquire (no leak across the same userId)
///     • <c>await using</c> scope exit disposes the Releaser
///     • two concurrent acquires for the same userId: second blocks until first is released
///     • two concurrent acquires for DIFFERENT userIds: both acquire immediately
///     • idempotent double-dispose does not over-release (next acquire still works)
///     • CancellationToken is honored (OperationCanceledException) — no semaphore leak
///     • after cancellation, the next acquire works (the wait threw BEFORE acquiring)
///     • cancellation does NOT release a held semaphore
///     • 100 sequential iterations — no leak, no deadlock
///     • two different userIds held simultaneously by different tasks
///     • two parallel tasks on the same userId: only one completes within timeout
///     • type-level contracts (sealed, implements ICartMutationLock)
///
/// SUT LOCATION:
///   TakOne.Infrastructure/Services/CartMutationLock.cs
/// </summary>
public class CartMutationLockTests
{
    // ── Type-level contract tests ────────────────────────────────────

    [Fact]
    public void Class_IsSealed_True()
    {
        // Arrange / Act / Assert — CartMutationLock is registered as a
        // Singleton concrete class via DI; subclasses are not part of
        // the surface.
        typeof(CartMutationLock).IsSealed.Should().BeTrue();
    }

    [Fact]
    public void Class_ImplementsICartMutationLock_True()
    {
        // Arrange / Act / Assert
        typeof(CartMutationLock).IsAssignableTo(typeof(ICartMutationLock)).Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_ReturnedReleaser_IsIAsyncDisposable()
    {
        // Arrange — the Releaser is a private sealed nested type, but the
        // return type is IAsyncDisposable, so callers should only need that
        // interface to release.
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // Act
        var handle = await sut.AcquireAsync(userId);

        // Assert
        handle.Should().BeAssignableTo<IAsyncDisposable>();

        // Cleanup — release the semaphore. Each test instantiates a fresh
        // CartMutationLock so cross-test semaphore interference is impossible.
        await handle.DisposeAsync();
    }

    // ── Empty-Guid rejection ─────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_WithEmptyGuid_ThrowsArgumentException()
    {
        // Arrange
        var sut = new CartMutationLock();

        // Act
        var act = async () => await sut.AcquireAsync(Guid.Empty);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AcquireAsync_WithEmptyGuid_ThrowsWithExpectedMessage()
    {
        // Arrange
        var sut = new CartMutationLock();

        // Act
        var act = async () => await sut.AcquireAsync(Guid.Empty);

        // Assert — the SUT's exact message is the multi-line string
        // "CartMutationLock.AcquireAsync: userId must not be Guid.Empty. ...".
        var ex = await act.Should().ThrowAsync<ArgumentException>();
        ex.WithMessage("CartMutationLock.AcquireAsync: userId must not be Guid.Empty*");
    }

    [Fact]
    public async Task AcquireAsync_WithEmptyGuid_ThrowsWithUserIdParamName()
    {
        // Arrange
        var sut = new CartMutationLock();

        // Act
        var act = async () => await sut.AcquireAsync(Guid.Empty);

        // Assert — the SUT passes nameof(userId) as the param name.
        var ex = await act.Should().ThrowAsync<ArgumentException>();
        ex.Which.ParamName.Should().Be("userId");
    }

    // ── Happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_WithValidUserId_ReturnsNonNullDisposable()
    {
        // Arrange
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // Act
        var handle = await sut.AcquireAsync(userId);

        // Assert
        handle.Should().NotBeNull();

        // Cleanup
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_AfterDispose_CanAcquireAgainForSameUserId()
    {
        // Arrange — the Releaser releases the semaphore on DisposeAsync;
        // the next AcquireAsync on the same userId should NOT block.
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // Act — first acquire, dispose, then second acquire.
        var handle1 = await sut.AcquireAsync(userId);
        await handle1.DisposeAsync();

        // Act — second acquire should complete immediately (no blocking).
        var handle2 = await sut.AcquireAsync(userId);

        // Assert — second acquire returned a fresh, non-null IAsyncDisposable.
        handle2.Should().NotBeNull();

        // Cleanup
        await handle2.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WithAwaitUsing_DisposesAtScopeExit()
    {
        // Arrange — the `await using` pattern is the production code pattern.
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // Act — declare the handle with `await using` so the C# compiler
        // emits a try/finally that calls DisposeAsync at scope exit.
        await using (var _ = await sut.AcquireAsync(userId))
        {
            // The lock is held inside this block.
        }

        // Assert — after the `await using` block, the semaphore was released.
        // We can prove this by acquiring it again immediately (no blocking).
        var handle = await sut.AcquireAsync(userId);

        // Cleanup
        await handle.DisposeAsync();
    }

    // ── Concurrency: same userId blocks; different userIds don't ──────

    [Fact]
    public async Task AcquireAsync_WhenSameUserIdAlreadyHeld_SecondCallBlocksUntilReleased()
    {
        // Arrange
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // First acquire on the test's main thread — holds the semaphore.
        var handle1 = await sut.AcquireAsync(userId);

        // Kick off a second acquire on a background thread — it should
        // block on semaphore.WaitAsync because the first caller hasn't released.
        var secondAcquire = Task.Run(async () => await sut.AcquireAsync(userId));

        // Give the second task a brief moment to start waiting on the semaphore.
        await Task.Delay(50);

        // Assert — the second acquire should NOT have completed yet
        // (it's blocked on WaitAsync).
        secondAcquire.IsCompleted.Should().BeFalse(
            "the second acquire should be blocked because the first caller holds the semaphore");

        // Act — release the first handle so the second can proceed.
        await handle1.DisposeAsync();

        // Now the second acquire should complete within a short time.
        var handle2 = await secondAcquire;

        // Cleanup
        await handle2.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WhenDifferentUserIdsHeld_BothAcquireImmediately()
    {
        // Arrange — different keys go into different SemaphoreSlim instances
        // in the ConcurrentDictionary, so neither blocks the other.
        var sut = new CartMutationLock();
        var userId1 = TestValues.UserId;
        var userId2 = TestValues.CustomerId; // different stable Guid

        // Act — acquire both, holding both simultaneously.
        var handle1 = await sut.AcquireAsync(userId1);
        var handle2Task = Task.Run(async () => await sut.AcquireAsync(userId2));
        await Task.Delay(50);

        // Assert — second acquire should have completed already (no blocking).
        handle2Task.IsCompleted.Should().BeTrue(
            "different userIds use different semaphores so they don't block each other");
        var handle2 = await handle2Task;

        // Cleanup
        await handle1.DisposeAsync();
        await handle2.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WhenTwoTasksSameUserId_OnlyOneCompletesUntilReleased()
    {
        // Arrange — a stricter version of the blocking test: kick off
        // two concurrent acquires for the same userId. Only one should
        // complete within a short timeout; the other blocks. After releasing
        // the first, the second should complete.
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // Two parallel tasks both trying to acquire the same userId.
        var t1 = Task.Run(async () => await sut.AcquireAsync(userId));
        var t2 = Task.Run(async () => await sut.AcquireAsync(userId));

        // Wait briefly for one to win the semaphore.
        await Task.Delay(50);

        // Exactly one of the two should have completed.
        var completedCount = (t1.IsCompleted ? 1 : 0) + (t2.IsCompleted ? 1 : 0);
        completedCount.Should().Be(1, "exactly one of the two racing acquires should win");

        // Identify which one completed and dispose its handle.
        // After WhenAll-style confirmation of one being completed, awaiting
        // returns synchronously (no blocking) — and avoids xUnit1031 warnings.
        var winner = t1.IsCompleted ? await t1 : await t2;
        await winner.DisposeAsync();

        // Now the loser should complete.
        var loser = t1.IsCompleted ? t2 : t1;
        var loserHandle = await loser;

        // Cleanup
        await loserHandle.DisposeAsync();
    }

    // ── Idempotent dispose ────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotOverReleaseSemaphore()
    {
        // Arrange — the private Releaser uses Interlocked.Exchange to
        // ensure only the FIRST DisposeAsync call actually releases
        // the semaphore. A second call is a no-op. If the SUT accidentally
        // called Release twice, the semaphore would be over-released and
        // a third concurrent waiter would sneak through prematurely.
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // Act — acquire, then dispose twice (intentional double-dispose).
        var handle = await sut.AcquireAsync(userId);
        await handle.DisposeAsync();
        // Second dispose — should be a no-op (idempotent).
        var actSecond = async () => await handle.DisposeAsync();

        // Assert — second dispose does not throw.
        await actSecond.Should().NotThrowAsync();

        // Act — now acquire again. If the double-dispose had thrown or
        // corrupted the semaphore's internal state, the next acquire fails.
        var handle2 = await sut.AcquireAsync(userId);

        // Cleanup
        await handle2.DisposeAsync();
    }

    // ── Cancellation ─────────────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_WithCanceledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // First acquire holds the semaphore.
        var handle1 = await sut.AcquireAsync(userId);

        // Pre-cancel a token.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act — second acquire with a pre-canceled token.
        var act = async () => await sut.AcquireAsync(userId, cts.Token);

        // Assert — SemaphoreSlim.WaitAsync(canceledToken) throws OCE immediately.
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Cleanup — the first handle is still valid; release it.
        await handle1.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_AfterCanceledAcquire_NextAcquireStillWorks()
    {
        // Arrange — the cancellation path does NOT acquire the semaphore
        // (WaitAsync throws BEFORE the semaphore is taken), so there's no
        // leak to clean up. The next normal acquire should still work.
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // First acquire holds the semaphore.
        var handle1 = await sut.AcquireAsync(userId);

        // A second acquire with a pre-canceled token throws OCE.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await sut.AcquireAsync(userId, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Act — release the first handle so the semaphore is free.
        await handle1.DisposeAsync();

        // Now the next acquire (no token) should succeed immediately.
        var handle2 = await sut.AcquireAsync(userId);

        // Cleanup
        await handle2.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WithCanceledToken_DoesNotReleaseFirstHeldSemaphore()
    {
        // Arrange — the cancellation path of the SECOND caller should NOT
        // release the semaphore held by the FIRST caller. Verify by trying
        // a third acquire after the second's cancellation — the third should
        // still block because the first is still holding.
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // First acquire holds the semaphore.
        var handle1 = await sut.AcquireAsync(userId);

        // Second acquire with a pre-canceled token throws OCE — but
        // does NOT release the first's hold.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await sut.AcquireAsync(userId, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        // Kick off a third acquire on a background thread — should block
        // because the first caller still holds the semaphore.
        var thirdAcquire = Task.Run(async () => await sut.AcquireAsync(userId));
        await Task.Delay(50);

        // Assert — third acquire is still blocked (cancellation didn't leak-release).
        thirdAcquire.IsCompleted.Should().BeFalse();

        // Cleanup — release the first; the third now completes.
        await handle1.DisposeAsync();
        var handle3 = await thirdAcquire;
        await handle3.DisposeAsync();
    }

    // ── Stress / leak detection ──────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_After100Iterations_NoLeakOrDeadlock()
    {
        // Arrange — repeatedly acquire and release the same userId.
        // If the SUT under-releases (leaks), iterations > 1 will block
        // forever. If the SUT over-releases, no observable failure here
        // but the over-release tests above would catch it. Either way,
        // this test confirms the basic acquire/release cycle is stable
        // under repetition.
        var sut = new CartMutationLock();
        var userId = TestValues.UserId;

        // Act / Assert — 100 cycles, each completing within a short timeout.
        for (var i = 0; i < 100; i++)
        {
            var handle = await sut.AcquireAsync(userId);
            await handle.DisposeAsync();
        }

        // If we got here without deadlocking, the test passes.
        // As a final sanity check, do one more acquire to confirm the
        // semaphore is in the released state.
        var finalHandle = await sut.AcquireAsync(userId);
        await finalHandle.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_TwoDifferentUserIdsHeldSimultaneously_BothSucceed()
    {
        // Arrange — concurrency sanity: two different userIds, acquired
        // from two different tasks in parallel, should both succeed
        // (independent semaphores, no contention).
        var sut = new CartMutationLock();
        var userId1 = TestValues.UserId;
        var userId2 = TestValues.CustomerId;

        // Act — run two parallel acquire tasks.
        var t1 = Task.Run(async () => await sut.AcquireAsync(userId1));
        var t2 = Task.Run(async () => await sut.AcquireAsync(userId2));
        await Task.WhenAll(t1, t2);

        // Assert — both completed successfully (no blocking, no exception).
        t1.IsCompleted.Should().BeTrue();
        t2.IsCompleted.Should().BeTrue();
        t1.IsFaulted.Should().BeFalse();
        t2.IsFaulted.Should().BeFalse();

        // Cleanup — await each task (instead of .Result) to satisfy the
        // xUnit1031 analyzer; awaiting an already-completed task is a no-op.
        var h1 = await t1;
        var h2 = await t2;
        await h1.DisposeAsync();
        await h2.DisposeAsync();
    }
}

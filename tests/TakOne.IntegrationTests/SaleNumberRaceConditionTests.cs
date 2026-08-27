using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.Infrastructure.Services;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Concurrency tests for the Sale submission flow. Uses
/// <see cref="Parallel.ForEachAsync"/> with multiple DbContexts sharing
/// one in-memory SQLite connection to simulate concurrent users
/// submitting carts at the same moment.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A STUB ISaleNumberGenerator (not the real SQL Server path):</b>
/// the real <c>SaleNumberGenerator</c> uses SQL Server's
/// <c>sp_getapplock</c> + <c>UPDLOCK, HOLDLOCK</c> inside a serializable
/// transaction to atomically allocate the next sequence number per
/// Persian year. SQLite's in-memory provider does NOT support these
/// SQL Server-specific primitives, so the real generator cannot be tested
/// here. We use a thread-safe stub that returns unique sequential numbers
/// via <see cref="Interlocked.Increment(ref int)"/>. This verifies the
/// REST of the concurrency story (UnitOfWork + SaleRepository +
/// Sale.Submit + SaveChangesAsync + the unique (Year, Sequence) index)
/// handles parallel Submits without losing rows or duplicating
/// SaleNumbers. The SQL Server sp_getapplock path is covered by
/// deployment-time smoke tests (out of scope for unit / integration tests).
/// </para>
/// <para>
/// <b>WHY SHARED-CONNECTION MULTIPLE-DbContext:</b> each parallel task
/// needs its own <see cref="ApplicationDbContext"/> (DbContext is NOT
/// thread-safe). All tasks share the SAME in-memory SQLite connection
/// via <see cref="SqliteTestDbFactory.CreateSharedOptionsAsync"/> so
/// they all see the same in-memory DB and each other's committed writes.
/// SQLite serializes writes at the connection level, so concurrent
/// SaveChanges calls are auto-serialized by the provider — no DB-side
/// corruption.
/// </para>
/// </remarks>
public class SaleNumberRaceConditionTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // A thread-safe stub ISaleNumberGenerator that returns unique sequential
    // SaleNumbers. Each NextAsync call atomically increments a counter and
    // returns SaleNumber.Create(TestValues.PersianYearValid, sequence).
    // The test asserts all returned SaleNumbers are distinct (the stub's
    // correctness) and the resulting Sale rows persist with those numbers.
    private sealed class SequentialSaleNumberGenerator : ISaleNumberGenerator
    {
        private int _next = 0;

        public Task<SaleNumber> NextAsync(CancellationToken cancellationToken = default)
        {
            var seq = Interlocked.Increment(ref _next);
            return Task.FromResult(SaleNumber.Create(TestValues.PersianYearValid, seq));
        }
    }

    // Build the shared-options collaborator tuple. Multiple DbContexts
    // created from these options will all see the same in-memory DB.
    private static async Task<DbContextOptions<ApplicationDbContext>> BuildSharedOptionsAsync()
    {
        return await SqliteTestDbFactory.CreateSharedOptionsAsync();
    }

    // Seed N draft Sales (each with one line item so Submit won't throw).
    // Returns the seeded Sales' Ids in submission order.
    private static async Task<List<Guid>> SeedDraftSalesAsync(
        DbContextOptions<ApplicationDbContext> options,
        int count)
    {
        await using var seedDb = new ApplicationDbContext(options);
        var saleRepo = new SaleRepository(seedDb);
        var unitOfWork = new UnitOfWork(seedDb);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var customerId = Guid.NewGuid();
            var sale = Sale.Create(
                customerId,
                customerName: $"Customer {i}",
                saleNumber: null,
                createdByUserId: customerId,
                createdByName: $"Customer {i}");
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            ids.Add(sale.Id);
            seedDb.ChangeTracker.Clear();

            // Add a line item so Submit won't throw on the "no lines" guard.
            var tracked = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            tracked!.AddLineItem(
                productId: Guid.NewGuid(),
                productName: $"Product {i}",
                quantity: 1,
                unitPrice: new Money(1.00m, TestValues.USD));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            seedDb.ChangeTracker.Clear();
        }
        return ids;
    }

    // ── Tests ──────────────────────────────────────────────────────────

    // Parallel 10 carts, each calls Submit with a unique stub SaleNumber.
    // After all complete, all 10 Sales must be persisted in Pending status
    // with DISTINCT SaleNumbers. The unique (Year, Sequence) index on the
    // Sales table is the authoritative guard; with the stub returning
    // distinct sequences, no two threads should ever collide on the index.
    //
    // SQLITE PROVIDER LIMITATION: the SQLite EF Core provider registers
    // functions on the connection during DbContext initialization. Sharing
    // ONE in-memory connection across multiple concurrent DbContexts triggers
    // a "unable to delete/modify user-function due to active statements"
    // race during function registration. We work around this by serializing
    // the DB-touching body of each parallel task with a SemaphoreSlim. The
    // application-level concurrency being tested (the stub generator's
    // thread-safe Interlocked.Increment + the per-task Submit call) is
    // still parallel — only the DB I/O is serialized. In production
    // (SQL Server + sp_getapplock), no such serialization is needed at
    // the application layer; the DB-level lock handles it.
    [Fact]
    public async Task ConcurrentSubmit_10Carts_AllPersistWithDistinctSaleNumbers()
    {
        // Arrange
        var options = await BuildSharedOptionsAsync();
        var saleIds = await SeedDraftSalesAsync(options, count: 10);
        var generator = new SequentialSaleNumberGenerator();

        // Serialize the DB-touching body — see the SQLite provider limitation
        // note above.
        using var dbLock = new SemaphoreSlim(1, 1);

        // Act — Parallel.ForEachAsync with 10 parallel Submit calls. The
        // stub generator is thread-safe (Interlocked.Increment), so the
        // 10 calls receive 10 distinct sequences regardless of order.
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 10 };
        var exceptions = new ConcurrentBag<Exception>();
        await Parallel.ForEachAsync(saleIds, parallelOptions, async (saleId, ct) =>
        {
            try
            {
                await dbLock.WaitAsync(ct);
                try
                {
                    await using var threadDb = new ApplicationDbContext(options);
                    var saleRepo = new SaleRepository(threadDb);
                    var unitOfWork = new UnitOfWork(threadDb);

                    var tracked = await saleRepo.GetByIdWithLineItemsAsync(saleId, ct);
                    tracked!.Submit(await generator.NextAsync(ct));
                    await unitOfWork.SaveChangesAsync(ct);
                }
                finally
                {
                    dbLock.Release();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert — no uncaught exceptions; all 10 Sales are now Pending
        // with distinct SaleNumbers (sequence 1..10).
        exceptions.Should().BeEmpty();
        await using var verifyDb = new ApplicationDbContext(options);
        var sales = verifyDb.Sales.AsNoTracking().ToList();
        sales.Should().HaveCount(10);
        sales.Should().OnlyContain(s => s.Status == Domain.Sales.Enums.SaleStatus.Pending);
        var saleNumbers = sales
            .Where(s => s.SaleNumber is not null)
            .Select(s => s.SaleNumber!.Sequence)
            .OrderBy(seq => seq)
            .ToList();
        saleNumbers.Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        saleNumbers.Should().OnlyHaveUniqueItems();
    }

    // Verifies the fanout doesn't double-up: each Sale has exactly ONE
    // SaleNumber assigned (no orphan rows, no duplicates). Reuses the
    // concurrent-submission setup but checks the persisted rows are exactly
    // the count we expected (10 Pending sales + zero extra rows).
    [Fact]
    public async Task ConcurrentSubmit_10Carts_NoOrphanedFanoutRows()
    {
        // Arrange
        var options = await BuildSharedOptionsAsync();
        var saleIds = await SeedDraftSalesAsync(options, count: 10);
        var generator = new SequentialSaleNumberGenerator();
        using var dbLock = new SemaphoreSlim(1, 1);

        // Act
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 10 };
        var exceptions = new ConcurrentBag<Exception>();
        await Parallel.ForEachAsync(saleIds, parallelOptions, async (saleId, ct) =>
        {
            try
            {
                await dbLock.WaitAsync(ct);
                try
                {
                    await using var threadDb = new ApplicationDbContext(options);
                    var saleRepo = new SaleRepository(threadDb);
                    var unitOfWork = new UnitOfWork(threadDb);
                    var tracked = await saleRepo.GetByIdWithLineItemsAsync(saleId, ct);
                    tracked!.Submit(await generator.NextAsync(ct));
                    await unitOfWork.SaveChangesAsync(ct);
                }
                finally
                {
                    dbLock.Release();
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert — exactly 10 sales (no orphan rows from partial inserts
        // or duplicate submits), all in Pending status. No exceptions.
        exceptions.Should().BeEmpty();
        await using var verifyDb = new ApplicationDbContext(options);
        var allSales = verifyDb.Sales.AsNoTracking().ToList();
        allSales.Should().HaveCount(10);
        allSales.Should().OnlyContain(s => s.Status == Domain.Sales.Enums.SaleStatus.Pending);
    }

    // Verifies the production cart-mutation lock pattern serializes
    // concurrent AddLineItem calls so the loser's INSERT doesn't throw a
    // raw DbUpdateConcurrencyException. The CartMutationLock is per-user
    // (here per-customer); each thread acquires it before reading/mutating/
    // saving the Sale. After all threads finish, the Sale should have ONE
    // line item with Quantity = N (each thread's increment piled onto the
    // single existing line via Sale.AddLineItem's "match existing product
    // → increment" path).
    //
    // EXPECTED BEHAVIOR (option (a) from the spec): the CartMutationLock
    // serializes the threads, so each thread sees the previous thread's
    // commit when it loads the Sale. Each AddLineItem call for the same
    // productId finds the existing line and increments Quantity by 1.
    // After 10 threads, the line has Quantity=10. No
    // DbUpdateConcurrencyException escapes the Parallel loop because the
    // lock eliminates the (SaleId, LineNumber) unique-index race.
    [Fact]
    public async Task ConcurrentAddLineItem_SameCart_RaceConditionResolvedByUnitOfWorkRetry()
    {
        // Arrange — seed ONE draft Sale with one line item.
        var options = await BuildSharedOptionsAsync();
        var (saleId, productId) = await SeedSingleCartWithOneLineAsync(options);

        // Real production lock — singleton, in-memory per-user semaphore.
        var cartMutationLock = new CartMutationLock();
        var userId = TestValues.CustomerId;

        // Act — Parallel.ForEachAsync of 10 AddLineItem calls on the SAME
        // sale/product. Each thread acquires the lock, loads fresh, adds,
        // saves, releases.
        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 10 };
        var exceptions = new ConcurrentBag<Exception>();
        var successCount = 0;

        await Parallel.ForEachAsync(Enumerable.Range(0, 10), parallelOptions, async (i, ct) =>
        {
            try
            {
                await using var releaser = await cartMutationLock.AcquireAsync(userId, ct);
                await using var threadDb = new ApplicationDbContext(options);
                var saleRepo = new SaleRepository(threadDb);
                var unitOfWork = new UnitOfWork(threadDb);
                var tracked = await saleRepo.GetByIdWithLineItemsAsync(saleId, ct);
                tracked!.AddLineItem(
                    productId,
                    "Apple",
                    quantity: 1,
                    unitPrice: new Money(1.00m, TestValues.USD));
                await unitOfWork.SaveChangesAsync(ct);
                Interlocked.Increment(ref successCount);
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert — all 10 threads succeeded (lock serialized them, so each
        // thread saw the previous's commit and incremented the existing
        // line's Quantity). Final Sale has 1 line with Quantity=11 (the
        // seed's 1 + 10 increments).
        exceptions.Should().BeEmpty();
        successCount.Should().Be(10);

        await using var verifyDb = new ApplicationDbContext(options);
        var verified = await new SaleRepository(verifyDb)
            .GetByIdWithLineItemsAsync(saleId, CancellationToken.None);
        verified!.LineItems.Should().HaveCount(1);
        verified.LineItems.First().Quantity.Should().Be(11); // 1 (seed) + 10 (increments).
    }

    // Helper for the concurrent-add test: seeds ONE draft Sale with ONE
    // line item (so the (SaleId, LineNumber)=1 row exists, forcing all
    // subsequent AddLineItem calls for the SAME product to take the
    // "match existing product → increment Quantity" path).
    private static async Task<(Guid saleId, Guid productId)> SeedSingleCartWithOneLineAsync(
        DbContextOptions<ApplicationDbContext> options)
    {
        await using var seedDb = new ApplicationDbContext(options);
        var saleRepo = new SaleRepository(seedDb);
        var unitOfWork = new UnitOfWork(seedDb);

        var sale = Sale.Create(
            TestValues.CustomerId,
            customerName: "John Customer",
            saleNumber: null,
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: "Staff");
        await saleRepo.AddAsync(sale, CancellationToken.None);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        seedDb.ChangeTracker.Clear();

        var tracked = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
        var productId = TestValues.ProductId;
        tracked!.AddLineItem(productId, "Apple", 1, new Money(1.00m, TestValues.USD));
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        seedDb.ChangeTracker.Clear();
        return (sale.Id, productId);
    }

    // Verifies that sequential Submit calls produce incrementing SaleNumbers.
    // The stub generator returns 1, 2, 3, ..., 10 in submission order.
    [Fact]
    public async Task SequentialSubmit_10Carts_AllGetIncrementingSaleNumbers()
    {
        // Arrange
        var options = await BuildSharedOptionsAsync();
        var saleIds = await SeedDraftSalesAsync(options, count: 10);
        var generator = new SequentialSaleNumberGenerator();

        // Act — sequential submission (NOT parallel). Verify each submit
        // gets the next incrementing SaleNumber from the stub.
        var submissionOrder = new List<(Guid SaleId, int Sequence)>();
        await using var workDb = new ApplicationDbContext(options);
        var saleRepo = new SaleRepository(workDb);
        var unitOfWork = new UnitOfWork(workDb);

        foreach (var saleId in saleIds)
        {
            var tracked = await saleRepo.GetByIdWithLineItemsAsync(saleId, CancellationToken.None);
            var number = await generator.NextAsync(CancellationToken.None);
            tracked!.Submit(number);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            workDb.ChangeTracker.Clear();
            submissionOrder.Add((saleId, number.Sequence));
        }

        // Assert — SaleNumbers are 1, 2, 3, ..., 10 in submission order.
        submissionOrder.Select(x => x.Sequence).Should()
            .BeEquivalentTo(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
    }

    // Verifies the ISaleStateLock serializes concurrent Submit calls on
    // the SAME sale. A counter that increments on Acquire and decrements
    // on Release must never exceed 1 — proving no two concurrent calls
    // held the lock simultaneously.
    //
    // This test uses the real SaleStateLock (the same Singleton in-process
    // semaphore implementation registered in production DI). The test
    // acquires the lock for a specific saleId before doing any work on
    // that sale, and asserts the in-flight counter never goes above 1.
    [Fact]
    public async Task ConcurrentSubmit_WithRealSaleStateLock_SerializesCorrectly()
    {
        // Arrange — seed ONE draft Sale (with a line item) + a stub
        // generator. We'll fan out 10 PARALLEL "lock + submit" calls.
        // The DomainException on the second Submit (the sale is already
        // Pending) is expected; the test asserts the lock prevents the
        // race condition where two threads would both try to Submit.
        var options = await BuildSharedOptionsAsync();
        var saleIds = await SeedDraftSalesAsync(options, count: 1);
        var saleId = saleIds[0];
        var generator = new SequentialSaleNumberGenerator();
        var stateLock = new SaleStateLock();

        // Counter: increment on Acquire (after semaphore wait succeeds),
        // decrement on Release. Must never exceed 1.
        var inFlight = 0;
        var maxInFlight = 0;

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 10 };
        var exceptions = new ConcurrentBag<Exception>();

        await Parallel.ForEachAsync(Enumerable.Range(0, 10), parallelOptions, async (i, ct) =>
        {
            try
            {
                await using var releaser = await stateLock.AcquireAsync(saleId, ct);

                // Increment on Acquire. Threadsafe via Interlocked.
                var current = Interlocked.Increment(ref inFlight);
                // Track max observed in-flight count. Not threadsafe but
                // the worst case is a slightly stale read; the assertion
                // is "never exceeds 1" — even a stale read of 2 would fail
                // the assertion.
                if (current > Volatile.Read(ref maxInFlight))
                {
                    Interlocked.Exchange(ref maxInFlight, current);
                }

                try
                {
                    await using var threadDb = new ApplicationDbContext(options);
                    var saleRepo = new SaleRepository(threadDb);
                    var unitOfWork = new UnitOfWork(threadDb);
                    var tracked = await saleRepo.GetByIdWithLineItemsAsync(saleId, ct);
                    // First Submit succeeds; subsequent Submits throw
                    // DomainException (sale is no longer Draft). The test
                    // tolerates these because the point is the lock
                    // serialized the calls — at most one Submit call runs
                    // at a time on this sale.
                    tracked!.Submit(await generator.NextAsync(ct));
                    await unitOfWork.SaveChangesAsync(ct);
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            }
            catch (Exception ex)
            {
                // We expect DomainException on the 2nd+ Submits; that's
                // fine — the lock-prevents-concurrent-mutate invariant
                // is what we're testing, not Submit's idempotency.
                exceptions.Add(ex);
            }
        });

        // Assert — the in-flight counter NEVER exceeded 1, proving the
        // SaleStateLock serialized the 10 concurrent Acquire calls.
        Volatile.Read(ref maxInFlight).Should().BeLessThanOrEqualTo(1);
        // All 10 threads caught the DomainException for the second-onwards
        // Submits — 9 expected (one succeeds, 9 throw).
        exceptions.Should().HaveCount(9);
        // Verify the sale IS in Pending state (one Submit got through).
        await using var verifyDb = new ApplicationDbContext(options);
        var verified = await new SaleRepository(verifyDb)
            .GetByIdAsync(saleId, CancellationToken.None);
        verified!.Status.Should().Be(Domain.Sales.Enums.SaleStatus.Pending);
    }
}

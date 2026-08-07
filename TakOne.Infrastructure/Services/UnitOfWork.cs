using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Infrastructure.Persistence;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>.
///
/// RESPONSIBILITY:
///   Provide the single <c>SaveChangesAsync</c> entry point that the
///   Application layer calls to commit ALL changes in a use case. By routing
///   every repository write through one DbContext and one SaveChanges, we
///   guarantee atomicity: either all changes commit together, or none do.
///
/// WHY A THIN WRAPPER:
///   The interface looks trivial (one method) — why not just inject
///   <c>ApplicationDbContext</c> directly into handlers? Three reasons:
///
///   1. DEPENDENCY INVERSION. The Application layer must NOT reference EF Core
///      or <c>ApplicationDbContext</c>. Exposing the DbContext would force the
///      Application project to take a project reference on Infrastructure,
///      which inverts the dependency direction and breaks the onion.
///
///   2. TESTABILITY. Handler unit tests can substitute a fake
///      <c>IUnitOfWork</c> that records SaveChanges calls without committing
///      to a real database. Mocking an interface is one line; mocking
///      <c>DbContext.SaveChangesAsync</c> is several (you have to mock the
///      DbSet, the ChangeTracker, etc.).
///
///   3. FUTURE HOOKS. The day we want to add cross-cutting concerns to
///      SaveChanges — domain-event dispatch (already planned for step 7e via
///      an <c>ISaveChangesInterceptor</c>), outbox message capture, audit
///      logging, optimistic-concurrency retries — there's exactly one place
///      to add them. If every handler called <c>DbContext.SaveChangesAsync</c>
///      directly, every handler would need updating.
///
/// INTERCEPTORS:
///   Domain-event dispatch and any other SaveChanges-side behaviors are
///   implemented as an <c>ISaveChangesInterceptor</c> registered on the
///   DbContext options (step 7e), NOT here. The interceptor runs inside EF
///   Core's SavingChanges event, BEFORE the actual SQL is issued — so it can
///   collect domain events from aggregates and enqueue them into Wolverine's
///   outbox as part of the same transaction.
///
///   This keeps <c>UnitOfWork</c> trivially simple — it's literally
///   <c>_db.SaveChangesAsync</c>. The complexity lives in the interceptor,
///   where it belongs.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly ApplicationDbContext _db;

    public UnitOfWork(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The returned <c>int</c> is the number of state entries written to the
    /// database. Handlers typically ignore it — they care about success vs.
    /// failure, not the row count. We surface it anyway for parity with EF
    /// Core's signature and for the rare case where a handler needs to verify
    /// "at least one row was affected" (defensive against a no-op bug).
    /// </remarks>
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implements <c>ChangeTracker.Clear()</c> — detaches every tracked entity
    /// in one call. This is the standard remedy for the Blazor Server scoped-
    /// DbContext stale-tracking issue described on
    /// <see cref="IUnitOfWork.ClearChangeTracker"/>.
    /// </remarks>
    public void ClearChangeTracker()
    {
        _db.ChangeTracker.Clear();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to <c>DbContext.Add(object)</c>, which starts tracking the
    /// entity in the <c>Added</c> state. The entity type must be registered
    /// on the DbContext (via DbSet or <c>ApplyConfigurationsFromAssembly</c>)
    /// — otherwise EF Core throws at runtime.
    ///
    /// Why not <c>AddAsync</c>? <c>AddAsync</c> is only needed when the
    /// entity's key generation strategy is DB-side (e.g. SQL Server IDENTITY).
    /// Our entities use client-side <c>Guid.NewGuid()</c> keys, so the
    /// synchronous <c>Add</c> is correct and slightly cheaper.
    /// </remarks>
    public void AddEntity(object entity)
    {
        _db.Add(entity);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// IMPLEMENTATION NOTES:
    /// <list type="number">
    ///   <item>
    ///     The retry catches two exception types:
    ///     <list type="bullet">
    ///       <item><c>DbUpdateConcurrencyException</c> — EF Core's signal
    ///             that a batched UPDATE/DELETE affected fewer rows than
    ///             expected. In our codebase this happens when two
    ///             concurrent INSERTs collide on a unique index (the
    ///             affected-row count for the conflicting INSERT is 0,
    ///             but EF Core expected 1), OR when a real optimistic-
    ///             concurrency token (RowVersion) mismatch occurs. We
    ///             don't currently use RowVersion on any entity, so the
    ///             first cause dominates.</item>
    ///       <item><c>DbUpdateException</c> wrapping a SQL Server
    ///             <c>SqlException</c> with error number 2627 (UNIQUE
    ///             CONSTRAINT violation) or 2601 (UNIQUE INDEX
    ///             violation on a non-clustered index). This is the
    ///             same race, surfaced differently when the unique
    ///             index fires AFTER the INSERT is partially
    ///             committed rather than as part of the affected-row
    ///             count.</item>
    ///     </list>
    ///   </item>
    ///   <item>
    ///     Between attempts, the change tracker is cleared via
    ///     <c>_db.ChangeTracker.Clear()</c>. This is mandatory: the
    ///     failed <c>SaveChangesAsync</c> leaves the DbContext in a
    ///     half-mutated state (some entities marked Modified, some
    ///     Added, the Sale's <c>LineItems</c> collection containing
    ///     the losing INSERT). Without clearing, the next attempt
    ///     would re-attach to the same stale graph and immediately
    ///     fail again.
    ///   </item>
    ///   <item>
    ///     Inter-attempt backoff is <c>50ms * attempt</c> (so 50ms,
    ///     100ms, ...). Linear, not exponential — concurrent UI
    ///     clicks resolve in milliseconds, so we want the retry to
    ///     fire quickly. If we ever retry on real DB contention
    ///     (multi-second holds), upgrade to exponential backoff with
    ///     jitter.
    ///   </item>
    ///   <item>
    ///     On the LAST attempt, the <c>when</c> filter on the catch
    ///     clauses evaluates to <c>false</c> (because
    ///     <c>attempt &lt; maxAttempts</c> is false), so the exception
    ///     propagates to the caller unchanged. The line after the loop
    ///     is unreachable; the <c>throw new InvalidOperationException</c>
    ///     exists only to satisfy the compiler's flow analysis.
    ///   </item>
    ///   <item>
    ///     MARS WARNING: When SQL Server MARS (Multiple Active Result
    ///     Sets) is enabled, EF Core cannot create savepoints inside
    ///     the active transaction. A failed <c>SaveChangesAsync</c>
    ///     therefore leaves the transaction in an indeterminate state
    ///     — the partial changes from the failed attempt may or may
    ///     not be rolled back. The retry clears the change tracker
    ///     (which prevents re-issuing the same conflicting SQL), but
    ///     the underlying transaction may still be poisoned. For
    ///     maximum safety, MARS should be DISABLED in the connection
    ///     string (<c>MultipleActiveResultSets=False</c>); with MARS
    ///     off, EF Core uses savepoints, and a failed
    ///     <c>SaveChangesAsync</c> rolls back cleanly to the
    ///     savepoint, leaving the transaction safe for retry.
    ///     <b>The <c>TakOneDatabaseOptions.EnsureValid</c> startup
    ///     guard now enforces this — the application refuses to start
    ///     if MARS is on, so this scenario should never occur at
    ///     runtime.</b> If you ever see MARS-related warnings in the
    ///     logs after the guard is in place, it means the guard was
    ///     bypassed (e.g. someone removed the check) — investigate
    ///     immediately. The historical symptom of MARS poisoning was
    ///     repeated <c>DbUpdateConcurrencyException</c>s on
    ///     <c>CreateOrAppendSaleCommand</c> where every retry failed
    ///     identically because the Wolverine-managed transaction was
    ///     indeterminate.
    ///   </item>
    /// </list>
    /// </para>
    /// </remarks>
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        if (operation is null)
            throw new ArgumentNullException(nameof(operation));
        if (maxAttempts < 1)
            maxAttempts = 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                // The failed SaveChanges left the change tracker in a
                // half-mutated state. Clear it so the retry's re-query
                // returns a fresh, untracked graph.
                _db.ChangeTracker.Clear();

                // Brief linear backoff. Concurrent UI clicks resolve in
                // milliseconds; we want the retry to fire quickly.
                await Task.Delay(50 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (attempt < maxAttempts && IsUniqueConstraintViolation(ex))
            {
                _db.ChangeTracker.Clear();
                await Task.Delay(50 * attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        // Unreachable: the `when` filter on the final attempt's catch
        // clauses evaluates to false, so the exception propagates
        // directly. This throw exists only to satisfy the compiler's
        // definite-return flow analysis.
        throw new InvalidOperationException(
            "ExecuteWithRetryAsync exhausted all attempts without returning a value or propagating an exception. " +
            "This indicates a logic error in the retry loop.");
    }

    /// <summary>
    /// Walks the <c>InnerException</c> chain of a
    /// <see cref="DbUpdateException"/> looking for a SQL Server
    /// <c>SqlException</c> whose <c>Number</c> indicates a unique-
    /// constraint violation.
    /// </summary>
    /// <remarks>
    /// SQL Server error numbers we treat as "unique constraint violation":
    /// <list type="bullet">
    ///   <item><c>2627</c> — Violation of UNIQUE KEY constraint
    ///         (raised when a UNIQUE constraint on the table is violated).</item>
    ///   <item><c>2601</c> — Cannot insert duplicate key row in object
    ///         (raised when a UNIQUE INDEX on the table is violated).</item>
    /// </list>
    /// Both are retryable in the "double-add-to-cart" race: the losing
    /// INSERT failed because the winning INSERT committed first. After
    /// clearing the change tracker and re-loading the aggregate, the
    /// retry sees the new state and either succeeds (no longer a
    /// duplicate INSERT — instead, an UPDATE that increments the
    /// existing line's Quantity) or returns a clean business-rule
    /// failure (e.g. stock exhausted by the concurrent winner).
    /// </remarks>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is Microsoft.Data.SqlClient.SqlException sql)
            {
                return sql.Number == 2627  // UNIQUE CONSTRAINT violation
                    || sql.Number == 2601; // UNIQUE INDEX (non-clustered) violation
            }
        }
        return false;
    }
}
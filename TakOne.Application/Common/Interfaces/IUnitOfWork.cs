namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Coordinates persistence across multiple repositories.
/// Ensures all changes in a single use case are saved atomically.
///
/// Why no Update method?
///   EF Core tracks changes to entities loaded from the database. When a handler
///   loads an aggregate, modifies it, and calls SaveChangesAsync(), EF Core
///   automatically detects the changes and generates UPDATE statements.
///   An explicit Update method is redundant.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detaches ALL tracked entities from the change tracker.
    /// Call this at the START of a handler that loads an existing aggregate
    /// and modifies it (e.g. AddSubCategory, AddSubSubCategory) when running
    /// under Blazor Server.
    ///
    /// WHY THIS EXISTS:
    ///   In Blazor Server, the DbContext is scoped to the user's CIRCUIT
    ///   (the entire SignalR connection), not to a single HTTP request. This
    ///   means the change tracker accumulates entities across multiple
    ///   operations: a Category loaded by GetActiveCategoriesQuery on page
    ///   load is STILL tracked when the user later clicks "Add sub-category".
    ///
    ///   When the handler then calls GetByIdWithHierarchyAsync, EF Core's
    ///   identity resolution returns the ALREADY-TRACKED instance. The
    ///   .Include(SubCategories) query merges into it. On SaveChanges,
    ///   DetectChanges() (called by EF Core + Wolverine's DomainEventScraper
    ///   interceptor) sees the _subCategories collection change and may mark
    ///   the parent Category as Modified — generating a spurious UPDATE that
    ///   fails with DbUpdateConcurrencyException ("expected 1, affected 0").
    ///
    ///   Clearing the tracker at handler entry ensures the aggregate is
    ///   loaded FRESH, with no stale tracking state. Only the new child
    ///   entity ends up in the Added state; the parent stays Unchanged.
    ///
    /// WHY NOT ALWAYS CALL IT:
    ///   Handlers that create a NEW aggregate (CreateCategoryCommandHandler,
    ///   CreateStaffCommandHandler, etc.) don't need this — they call
    ///   repository.AddAsync(), which explicitly tracks the new entity.
    ///   Clearing the tracker there would be harmless but pointless.
    ///
    /// NOTE:
    ///   In practice, <c>ClearChangeTracker</c> alone is NOT enough to
    ///   prevent the stale-tracking bug — the subsequent tracked
    ///   <see cref="ICategoryRepository.GetByIdWithHierarchyAsync"/> call
    ///   re-attaches the parent + children, and DetectChanges can still
    ///   mark them Modified. The reliable pattern is to load the aggregate
    ///   <c>AsNoTracking</c> (see
    ///   <see cref="ICategoryRepository.GetByIdWithHierarchyNoTrackingAsync"/>)
    ///   and then explicitly track ONLY the new child via
    ///   <see cref="AddEntity"/>. <c>ClearChangeTracker</c> is retained
    ///   for backward compatibility and for handlers that genuinely need
    ///   a fresh tracked load.
    /// </summary>
    void ClearChangeTracker();

    /// <summary>
    /// Explicitly marks the given entity as <c>Added</c> in EF Core's change
    /// tracker. Use this when you've loaded an aggregate <c>AsNoTracking</c>
    /// (to avoid the Blazor Server scoped-DbContext stale-tracking bug) and
    /// want to persist a SINGLE new child entity.
    ///
    /// WHY THIS IS NEEDED:
    ///   When a handler loads a parent Category via
    ///   <c>GetByIdWithHierarchyNoTrackingAsync</c>, the parent and its
    ///   existing children are NOT tracked. Mutating the parent's
    ///   <c>_subCategories</c> collection (via <c>AddSubCategory</c>) has
    ///   no effect on the tracker — the new SubCategory is just an in-memory
    ///   object. To persist it, the handler must explicitly tell EF Core
    ///   "track this as a new entity" — that's what <c>AddEntity</c> does.
    ///
    ///   After <c>AddEntity(newSubCategory)</c>, the change tracker contains
    ///   exactly ONE entity (the new SubCategory) in the <c>Added</c> state.
    ///   <c>SaveChangesAsync</c> then generates exactly one INSERT and zero
    ///   UPDATEs — the parent and siblings are untracked, so they cannot
    ///   be marked <c>Modified</c> and no spurious UPDATE can be generated.
    ///
    /// WHY THIS IS PREFERRED OVER <c>ClearChangeTracker</c>:
    ///   <c>ClearChangeTracker</c> clears the tracker, but a subsequent
    ///   tracked query re-populates it. <c>AddEntity</c> works with an
    ///   <c>AsNoTracking</c> read, so the tracker stays empty except for
    ///   the single new entity the handler wants to persist.
    /// </summary>
    void AddEntity(object entity);

    /// <summary>
    /// Executes <paramref name="operation"/> inside a retry loop that
    /// transparently handles EF Core concurrency conflicts
    /// (<c>DbUpdateConcurrencyException</c>) and unique-constraint violations
    /// by clearing the change tracker and re-running the operation.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// <b>WHY THIS EXISTS:</b>
    /// Several command handlers — most notably
    /// <c>CreateOrAppendSaleCommandHandler</c> — are vulnerable to the
    /// "double-add-to-cart" race. Two concurrent invocations both load the
    /// same draft Sale (with no line for this product), both compute
    /// <c>LineNumber = 1</c> for the new line, and both INSERT. The
    /// <c>(SaleId, LineNumber)</c> unique index lets only one win; the
    /// loser's <c>SaveChangesAsync</c> fails with a unique-constraint
    /// violation surfaced as <c>DbUpdateConcurrencyException</c>
    /// ("expected to affect 1 row(s), but actually affected 0 row(s)").
    /// The retry loop resolves this by re-loading the now-modified
    /// aggregate and re-applying the mutation from scratch.
    /// </para>
    /// <para>
    /// <b>WHY THIS LIVES ON <c>IUnitOfWork</c> (not in the handler):</b>
    /// The Application layer has no reference to EF Core — it cannot
    /// catch <c>DbUpdateConcurrencyException</c> directly, because that
    /// type lives in the <c>Microsoft.EntityFrameworkCore</c> package
    /// which the Application project intentionally does not reference.
    /// Centralizing the retry policy on <c>IUnitOfWork</c> keeps the
    /// Application layer clean of persistence concerns while still
    /// giving handlers a way to opt into retry-aware execution.
    /// The implementation (in <c>Infrastructure.Services.UnitOfWork</c>)
    /// translates the EF Core exceptions into a retry decision internally;
    /// the contract exposed here is purely "run this, retry on transient
    /// persistence conflicts".
    /// </para>
    /// <para>
    /// <b>CONTRACT FOR <paramref name="operation"/>:</b>
    /// <list type="bullet">
    ///   <item>The delegate MUST be idempotent under re-execution.</item>
    ///   <item>It MUST re-query the current state of any aggregates it
    ///         mutates (e.g. re-load the draft Sale) on every attempt —
    ///         it MUST NOT assume entities loaded before the call are
    ///         still tracked, because the retry clears the change
    ///         tracker between attempts.</item>
    ///   <item>It MAY return a <c>Result&lt;T&gt;</c> for business-rule
    ///         failures (stock check, purchase-limit, etc.) — those are
    ///         returned to the caller unchanged, NOT retried. Only
    ///         infrastructure-level exceptions trigger retry.</item>
    ///   <item>The passed <c>CancellationToken</c> must be threaded into
    ///         every async DB call inside the delegate, so cancellation
    ///         aborts both the in-flight operation and any pending retry.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>WHEN NOT TO USE THIS:</b>
    /// <list type="bullet">
    ///   <item>Handlers that perform a single, non-conflicting INSERT
    ///         (e.g. <c>CreateCategoryCommandHandler</c>) don't need it.</item>
    ///   <item>Handlers whose <c>SaveChangesAsync</c> cannot legally
    ///         conflict at the DB level don't need it.</item>
    ///   <item>Handlers that load an aggregate AsNoTracking and use
    ///         <see cref="AddEntity"/> to persist only a single new child
    ///         don't need it — the AsNoTracking + single-add pattern is
    ///         already conflict-free.</item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="operation">The idempotent operation to execute. Receives
    /// the same <c>CancellationToken</c> as <paramref name="cancellationToken"/>;
    /// the delegate should forward it to every async call it makes.</param>
    /// <param name="maxAttempts">Maximum number of attempts. Defaults to 3.
    /// Must be ≥ 1. The operation is invoked AT MOST this many times;
    /// on the final attempt, any exception propagates to the caller
    /// unchanged.</param>
    /// <param name="cancellationToken">Forwarded to <paramref name="operation"/>
    /// AND used for the inter-retry backoff delay.</param>
    /// <typeparam name="T">The return type of the operation. For command
    /// handlers, this is typically <c>Result&lt;Guid&gt;</c> or
    /// <c>Result</c>.</typeparam>
    /// <returns>The result of <paramref name="operation"/> on the first
    /// successful attempt. If all attempts fail with a retryable exception,
    /// the final attempt's exception propagates to the caller.</returns>
    Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        int maxAttempts = 3,
        CancellationToken cancellationToken = default);
}
namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Per-sale in-memory semaphore lock for serializing sale state
/// transitions (Submit / Approve / Cancel / MarkAsInvoiced) on the same
/// sale row.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS EXISTS (race-condition fix)</b>: the existing
/// <see cref="ICartMutationLock"/> is keyed on <c>CustomerId</c> and only
/// acquired by cart-mutating handlers (AddItem, Update, Remove,
/// CreateOrAppend, Submit). It does NOT cover:
/// </para>
/// <list type="bullet">
///   <item><c>ApproveSaleCommandHandler</c></item>
///   <item><c>CancelSaleCommandHandler</c></item>
///   <item><c>MarkAsInvoicedCommandHandler</c></item>
/// </list>
/// <para>
/// This means concurrent Submit × Approve on the same sale can race:
/// the loser's SaveChanges throws <c>DbUpdateConcurrencyException</c>
/// (or worse, the loser's <c>wasApproved</c> snapshot in CancelSale is
/// stale by the time it commits, leading to wrong stock-restoration).
/// </para>
/// <para>
/// Acquiring <see cref="AcquireAsync"/> on the sale's Id at the start of
/// each state-transition handler serializes them per-sale: only one
/// transition runs at a time per sale. Combined with
/// <see cref="IUnitOfWork.ExecuteWithRetryAsync"/> for the
/// cross-instance race (multiple app instances hitting the same DB),
/// this eliminates the sale-state-transition race window.
/// </para>
/// <para>
/// <b>SINGLE-NODE LIMITATION</b>: this is a process-local
/// <c>ConcurrentDictionary&lt;Guid, SemaphoreSlim&gt;</c> — like
/// <see cref="ICartMutationLock"/>. For multi-node deployments, replace
/// the implementation with a SQL Server <c>sp_getapplock</c>-based
/// distributed lock (the project is currently single-node per the
/// deployment notes in <c>Program.cs</c>).
/// </para>
/// <para>
/// <b>LIFETIME</b>: must be <b>Singleton</b> (registered in
/// Infrastructure DI). Scoped would mean each request has its own
/// semaphore — defeating the purpose.
/// </para>
/// </remarks>
public interface ISaleStateLock
{
    /// <summary>
    /// Acquires an exclusive lock on the given <paramref name="saleId"/>.
    /// Await the returned <c>IAsyncDisposable</c> in a <c>using</c>
    /// statement so the lock is released when the handler returns
    /// (whether normally or via exception).
    /// </summary>
    /// <param name="saleId">The Id of the sale being transitioned. Must
    /// be non-empty.</param>
    /// <param name="cancellationToken">Cancellation token. Cancellation
    /// releases the wait but does NOT release a held lock — release is
    /// tied to the returned <c>IAsyncDisposable</c>'s
    /// <c>DisposeAsync</c>.</param>
    /// <returns>An <see cref="IAsyncDisposable"/> that releases the lock
    /// when disposed.</returns>
    /// <exception cref="ArgumentException">If <paramref name="saleId"/>
    /// is <see cref="Guid.Empty"/>.</exception>
    Task<IAsyncDisposable> AcquireAsync(Guid saleId, CancellationToken cancellationToken = default);
}

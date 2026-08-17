namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Provides a per-user async mutex used to serialize cart mutations for a
/// single customer. The 5 sale-mutating handlers (CreateOrAppendSale,
/// AddItemToSale, UpdateSaleLineItem, RemoveSaleLineItem,
/// QuickReorderLastSale) all acquire this lock before reading or writing
/// the customer's cart.
///
/// RATIONALE:
///   The existing Wolverine retry loop in CreateOrAppendSale handles the
///   DB-level race (concurrent inserts conflict on the
///   (SaleId,LineNumber) unique index, the loser retries and finds the
///   winner's INSERT). But that loop only catches DB constraint
///   violations — it does NOT prevent two concurrent invocations from
///   BOTH reading stale budget info and BOTH deciding their line fits
///   within the salary budget. The result would be two lines added that
///   together exceed the budget, even though each one alone was fine.
///
///   The per-user semaphore prevents this by serializing ALL cart
///   mutations for a given user. The second invocation waits for the
///   first to commit before even reading the cart / budget state — so
///   it sees the first invocation's commit when it computes the
///   "remaining budget" check.
///
/// WHY THE LOCK IS PER-USER (NOT PER-SALE):
///   A user can have only ONE active draft at a time (enforced by
///   GetActiveDraftForUserAsync). All 5 handlers operate on that single
///   draft. Locking per-user is equivalent to locking per-active-draft,
///   but cheaper (no DB lookup needed to find the draft's Id before
///   acquiring the lock — we already have the user's Id from the
///   command / sale.CustomerId).
///
/// WHY THE LOCK IS ACQUIRED ON sale.CustomerId, NOT currentUser.UserId:
///   When a staff member is building a sale on behalf of a customer
///   (AddItemToSale / UpdateSaleLineItem / RemoveSaleLineItem paths),
///   the cart being mutated belongs to the CUSTOMER, not the staff
///   member. Two staff members editing the same customer's cart must
///   serialize on the customer's lock — not their own. The Create /
///   Append and Quick Reorder paths are self-buy only, so
///   currentUser.UserId == sale.CustomerId, and either lock is
///   equivalent.
///
/// USAGE:
///   <code>
///   await using var _ = await cartMutationLock.AcquireAsync(sale.CustomerId, ct);
///   // ... read cart, mutate, save ...
///   </code>
///
///   The <c>await using</c> pattern guarantees the semaphore is
///   released even if the handler throws.
/// </summary>
public interface ICartMutationLock
{
    /// <summary>
    /// Acquires the per-user semaphore for the given customer. Returns
    /// an <see cref="IAsyncDisposable"/> that releases the semaphore
    /// when disposed. The caller should use <c>await using</c> to
    /// guarantee release on all exit paths (including exceptions).
    ///
    /// The method itself awaits the semaphore — so concurrent
    /// invocations for the same user will block here until the prior
    /// one releases.
    /// </summary>
    /// <param name="userId">
    /// The customer's user Id. Must not be <see cref="Guid.Empty"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token — if cancelled while waiting for the
    /// semaphore, the wait is aborted and
    /// <see cref="OperationCanceledException"/> is thrown.
    /// </param>
    Task<IAsyncDisposable> AcquireAsync(Guid userId, CancellationToken cancellationToken = default);
}
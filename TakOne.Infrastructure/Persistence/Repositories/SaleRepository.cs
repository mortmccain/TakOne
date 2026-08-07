using Ardalis.Specification;
using Ardalis.Specification.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ISaleRepository"/>.
///
/// SPECIFICATION PATTERN (Ardalis):
///   <see cref="GetPaginatedBySpecificationAsync"/> and
///   <see cref="GetAllBySpecificationAsync"/> accept an
///   <c>ISpecification&lt;Sale&gt;</c> from the Application layer. We feed it
///   to <see cref="SpecificationEvaluator"/>, which translates it into an
///   <c>IQueryable&lt;Sale&gt;</c> by applying (in order):
///     - Where clauses          (Query.Where)
///     - Includes               (Query.Include / .ThenInclude)
///     - OrderBy / OrderByDesc  (Query.OrderBy / Query.OrderByDescending)
///     - AsNoTracking           (Query.AsNoTracking)
///     - Skip / Take            (pagination — but we add this AFTER evaluation
///                                 so the spec doesn't have to know the page)
///
///   This separation means a single spec (e.g. SaleByCreatorSpecification)
///   works for both "all sales for this user" and "page 3 of sales for this
///   user" — the page parameters are the repository's concern, not the spec's.
///
/// TRACKING POLICY:
///   Read methods return TRACKED entities (same rationale as ProductRepository:
///   handlers load → mutate → SaveChanges). The spec evaluator does NOT add
///   AsNoTracking unless the spec itself declares <c>Query.AsNoTracking()</c>.
///   All current specs (SaleByCreatorSpecification, AllSalesSpecification) are
///   silent on this — they return tracked entities, which is correct for the
///   command-handler pattern but suboptimal for pure reads. If/when a query
///   handler needs AsNoTracking, add <c>Query.AsNoTracking()</c> to the spec.
///
/// DELETE INVARIANT:
///   <see cref="DeleteAsync"/> performs a defensive check that the Sale is in
///   <see cref="SaleStatus.Draft"/>. The Domain's <c>Sale.Cancel()</c> throws
///   for Drafts (drafts are hard-deleted, not cancelled) — but if a handler
///   accidentally calls DeleteAsync on a non-Draft sale, we want to fail loudly
///   rather than silently destroying a submitted/approved sale's audit trail.
/// </summary>
public sealed class SaleRepository : ISaleRepository
{
    // SpecificationEvaluator.Default is the standard, stateless evaluator.
    // It's safe to share across instances and threads. We hold it as a static
    // field so we don't allocate per call.
    private static readonly SpecificationEvaluator _evaluator = SpecificationEvaluator.Default;

    private readonly ApplicationDbContext _db;

    public SaleRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Sale?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // FindAsync: see ProductRepository.GetByIdAsync for rationale.
        // SaleNumber (owned) + Total (owned) auto-load. LineItems do NOT
        // (they're a navigation, not owned) — use GetByIdWithLineItemsAsync.
        return await _db.Sales.FindAsync(new object[] { id }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Sale?> GetByIdWithLineItemsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Eager-load the LineItems collection. Required by any handler that
        // inspects or mutates lines (AddItemToSale, UpdateSaleLineItem,
        // RemoveSaleLineItem, SubmitSale, ApproveSale, CancelSale).
        //
        // FirstOrDefaultAsync (not FindAsync) because FindAsync doesn't
        // support Include.
        return await _db.Sales
            .Include(s => s.LineItems)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Sale?> GetActiveDraftForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Defensive: Guid.Empty would match no Sale (CustomerId is non-nullable
        // in the schema and validated in the Sale factory), but we short-circuit
        // here to avoid sending a nonsensical query to SQL Server.
        if (userId == Guid.Empty)
        {
            return null;
        }

        // CustomerId == userId filter; Status == Draft; eager-load LineItems
        // (the caller — CreateOrAppendSaleCommandHandler — needs them to:
        //   1. check existing-quantity for stock aggregation
        //   2. add a new line OR increment an existing one
        // ).
        //
        // OrderByDescending(CreatedAtUtc) + FirstOrDefaultAsync picks the
        // MOST RECENT draft if (defensively) more than one exists. Per the
        // interface XML doc, this is the cleanup-friendly behavior: the
        // orphaned older draft remains in the DB but isn't surfaced.
        //
        // TRACKING: returns a TRACKED entity (no AsNoTracking) — the caller
        // is about to call sale.AddLineItem(...) + unitOfWork.SaveChangesAsync,
        // which requires the entity + its line items to be tracked so EF
        // detects the changes.
        return await _db.Sales
            .Include(s => s.LineItems)
            .Where(s => s.CustomerId == userId && s.Status == SaleStatus.Draft)
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PaginatedResult<Sale>> GetPaginatedBySpecificationAsync(
        ISpecification<Sale> specification,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Defensive: clamp page parameters. The handler also clamps, but a
        // future caller might bypass it. NEVER trust the caller with raw
        // OFFSET values — bad input produces nonsensical SQL.
        // ------------------------------------------------------------------
        pageNumber = pageNumber < 1 ? 1 : pageNumber;
        pageSize = pageSize < 1 ? 20 : pageSize;

        // ------------------------------------------------------------------
        // 1. Apply the spec to the Sales DbSet. The evaluator returns an
        //    IQueryable<Sale> with all Where/OrderBy/Include clauses from
        //    the spec already composed in. We do NOT add pagination here —
        //    the spec is a reusable query definition; pagination is a
        //    per-call concern.
        // ------------------------------------------------------------------
        var baseQuery = _evaluator.GetQuery(_db.Sales, specification);

        // ------------------------------------------------------------------
        // 2. Total count. We MUST count on the spec'd query (before pagination),
        //    so the count reflects the same filters the page reflects.
        //
        //    CountAsync translates to SELECT COUNT(*) FROM Sales WHERE [spec].
        //    Fast on indexed columns (CustomerId, CreatedByUserId, Status).
        // ------------------------------------------------------------------
        var totalCount = await baseQuery.CountAsync(cancellationToken);

        // ------------------------------------------------------------------
        // 3. Apply pagination. ORDER BY is already enforced by the spec
        //    (both SaleByCreatorSpecification and AllSalesSpecification add
        //    Query.OrderByDescending(s => s.CreatedAtUtc)), so OFFSET/FETCH
        //    won't throw.
        //
        //    If a future spec forgets to add OrderBy, EF Core will throw at
        //    runtime with a clear error — the handler-level test will catch
        //    this immediately. We do NOT add a default ordering here because
        //    silently adding one would mask the spec bug.
        // ------------------------------------------------------------------
        var items = await baseQuery
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<Sale>(items, totalCount, pageNumber, pageSize);
    }

    /// <inheritdoc />
    public async Task<List<Sale>> GetAllBySpecificationAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // No pagination — caller wants everything. Useful for reporting or
        // batch operations. The spec's OrderBy is preserved.
        var query = _evaluator.GetQuery(_db.Sales, specification);
        return await query.ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<Sale>> GetAllWithLineItemsBySpecificationAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // Same as GetAllBySpecificationAsync but eagerly includes line items
        // via EF Core's Include. Single round-trip — avoids N+1 when the
        // caller needs line items for many sales (e.g. dashboard aggregations).
        var query = _evaluator.GetQuery(_db.Sales.Include(s => s.LineItems), specification);
        return await query.ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Sale?> GetLastSubmittedSaleForUserAsync
        (
        Guid userId,
        CancellationToken cancellationToken = default
        )
    {
        // Defensive: Guid.Empty would match no Sale (CustomerId is non-nullable
        // in the schema and validated in the Sale factory).
        if (userId == Guid.Empty)
        {
            return null;
        }

        // CustomerId == userId filter; Status in {Pending, Approved, Invoiced}
        // (i.e. > Draft AND != Cancelled). Eager-load LineItems because the
        // Quick Reorder handler iterates the lines to re-add them to the draft.
        //
        // OrderByDescending(SubmittedAtUtc) picks the most-recently submitted
        // order. SubmittedAtUtc is null only for Drafts (which are already
        // excluded by the Status filter), so the ordering is well-defined.
        //
        // TRACKING: returns a TRACKED entity — the Quick Reorder handler reads
        // the line items but doesn't mutate THIS sale (it adds lines to the
        // user's DRAFT, not to this historical sale). Tracking is harmless
        // because we SaveChanges once at the end of the handler, and the
        // historical sale is never mutated in memory.
        return await _db.Sales
            .Include(s => s.LineItems)
            .Where(s => s.CustomerId == userId
                        && s.Status != SaleStatus.Draft
                        && s.Status != SaleStatus.Cancelled)
            .OrderByDescending(s => s.SubmittedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddAsync(Sale sale, CancellationToken cancellationToken = default)
    {
        // AddAsync queues the Sale (and reachable LineItems, since they're
        // part of the aggregate) for INSERT on SaveChanges. LineItems added
        // via Sale.AddLineItem are automatically tracked because they're
        // reachable from the tracked root.
        await _db.Sales.AddAsync(sale, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteAsync(Sale sale, CancellationToken cancellationToken = default)
    {
        // DEFENSIVE: only Drafts may be hard-deleted. The interface doc and
        // the domain's Sale.Cancel() doc both say this, but we enforce it
        // here too because the cost of a mistaken hard-delete on a submitted
        // sale is catastrophic (lost audit trail, broken downstream reports,
        // potential legal implications).
        //
        // We throw rather than silently no-op'ing — silent no-op would mask
        // the bug in the calling handler.
        //
        // We do NOT query the DB to verify Status; we trust the in-memory
        // entity passed by the handler (which loaded it via GetByIdAsync in
        // the same UoW). If the handler has a stale entity, that's a separate
        // concurrency issue the optimistic-concurrency check (RowVersion, if
        // we add one) would catch.
        if (sale.Status != SaleStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Cannot hard-delete Sale '{sale.Id}' because it is in status " +
                $"'{sale.Status}'. Only Draft sales may be hard-deleted. " +
                "For Pending/Approved sales, use Sale.Cancel() instead.");
        }

        // Remove queues the Sale (and reachable LineItems, via the cascade
        // delete configured in SaleConfiguration) for DELETE on SaveChanges.
        // Remove is synchronous — it just marks the entity for deletion in
        // the change tracker. The DB hit happens on SaveChangesAsync.
        _db.Sales.Remove(sale);

        return Task.CompletedTask;
    }
}
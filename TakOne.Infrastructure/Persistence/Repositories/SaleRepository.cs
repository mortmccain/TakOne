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
///   This separation means a single spec (e.g. SaleByCustomerSpecification)
///   works for both "all sales for this user" and "page 3 of sales for this
///   user" — the page parameters are the repository's concern, not the spec's.
///
/// TRACKING POLICY:
///   Read methods return TRACKED entities (same rationale as ProductRepository:
///   handlers load → mutate → SaveChanges). The spec evaluator does NOT add
///   AsNoTracking unless the spec itself declares <c>Query.AsNoTracking()</c>.
///   All current specs (SaleByCustomerSpecification, AllSalesSpecification,
///   SaleByApproverSpecification) are
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
        //    (both SaleByCustomerSpecification and AllSalesSpecification add
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

    /// <inheritdoc />
    public async Task<decimal> GetConsumedAmountForCustomerInWindowAsync(
        Guid customerId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Computes the customer's consumed salary-budget amount in the given
        // Persian-month window. See ISaleRepository.GetConsumedAmountForCustomerInWindowAsync
        // for the full rationale.
        //
        // The query is a single round-trip that sums the Total#Money#Amount
        // column across two buckets:
        //
        //   1. ACTIVE DRAFT CART (Status == Draft) — "cart reserves budget".
        //      At most one active draft per customer (enforced by the
        //      GetActiveDraftForUserAsync convention + the new ghost-draft
        //      cleanup in QuickReorderLastSaleCommandHandler). We SUM all
        //      drafts defensively — if a ghost draft slipped through, its
        //      Total is 0 anyway (no line items), so it contributes nothing.
        //
        //   2. SUBMITTED SALES (Status NOT IN (Draft, Cancelled)) with
        //      SubmittedAtUtc in [windowStartUtc, windowEndUtc). This
        //      covers Pending, Approved, Invoiced — anything that has
        //      left the cart state and hasn't been cancelled.
        //
        //      "Use it or lose it": cross-month cancellations do NOT
        //      refund to the new month. A sale submitted in month M and
        //      cancelled in month M+1 is simply not in M+1's
        //      [windowStartUtc, windowEndUtc) range — it was in M's range.
        //      So it doesn't count either way in M+1's window.
        //
        //      Cancellation refund within the same month: a sale submitted
        //      AND cancelled in month M IS still in M's window (SubmittedAtUtc
        //      is in M). But because Status == Cancelled, the WHERE clause
        //      excludes it. The refund is therefore implicit — no separate
        //      "refund" operation needed.
        //
        //      NOTE on SubmittedAtUtc: this column is NULL for Drafts and
        //      populated when the sale transitions to Pending (via
        //      Sale.Submit()). The query treats NULL as "outside the
        //      window" — drafts are caught by bucket 1 above.
        //
        // SQL TRANSLATION (rough):
        //   SELECT COALESCE(SUM(s.Total_Amount), 0)
        //   FROM Sales s
        //   WHERE s.CustomerId = @customerId
        //     AND (
        //       s.Status = 0   -- Draft (active cart)
        //       OR
        //       (s.Status NOT IN (0, 4)   -- not Draft, not Cancelled
        //        AND s.SubmittedAtUtc >= @windowStartUtc
        //        AND s.SubmittedAtUtc <  @windowEndUtc)
        //     )
        //
        //   (SaleStatus enum values: Draft=0, Pending=1, Approved=2,
        //    Invoiced=3, Cancelled=4)
        //
        // ASNOTRACKING: this is a pure read — the caller (SalaryBudgetService)
        // never mutates the Sale rows it sums. AsNoTracking keeps the query
        // result out of the change tracker entirely.
        // ------------------------------------------------------------------
        if (customerId == Guid.Empty)
        {
            // Defensive: Guid.Empty would match no Sale (CustomerId is
            // non-nullable in the schema), but we short-circuit here to
            // avoid sending a nonsensical query to SQL Server.
            return 0m;
        }

        var consumed = await _db.Sales
            .AsNoTracking()
            .Where(s => s.CustomerId == customerId
                        && (s.Status == SaleStatus.Draft
                            || (s.Status != SaleStatus.Draft
                                && s.Status != SaleStatus.Cancelled
                                && s.SubmittedAtUtc != null
                                && s.SubmittedAtUtc >= windowStartUtc
                                && s.SubmittedAtUtc < windowEndUtc)))
            .SumAsync(s => (decimal?)s.Total.Amount, cancellationToken);

        // SumAsync with a nullable cast returns null when no rows match.
        // Coalesce to 0 — empty carts are a normal state (no consumption).
        return consumed ?? 0m;
    }

    // ==================================================================
    // SCALAR + BOUNDED-SLICE IMPLEMENTATIONS
    // (Brutal Code Review v3 #23, Round 18-C — see ISaleRepository docs)
    // ==================================================================

    /// <inheritdoc />
    public async Task<int> CountBySpecificationAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // Spec evaluator produces an IQueryable<Sale> with all the spec's
        // Where/OrderBy/Include clauses applied. CountAsync ignores the
        // ORDER BY (SQL Server optimizes it out) and any Include (COUNT
        // doesn't need joined rows). The result is a single SQL
        // SELECT COUNT(*) FROM Sales WHERE [spec.Where].
        //
        // AsNoTracking: pure read — caller never mutates these sales.
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        return await query.CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountByStatusAsync(
        SaleStatus status,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // The .Where(s => s.Status == status) is composed ON TOP of the
        // spec's Where clauses. EF Core combines them into a single
        // SQL WHERE: WHERE [spec.Where] AND Status = @status.
        //
        // The Status index (SaleConfiguration adds HasIndex(s => s.Status))
        // makes the status filter fast for high-cardinality statuses
        // (Pending is the typical bottleneck — sales pile up while
        // approval is slow).
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        return await query.CountAsync(s => s.Status == status, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<decimal> SumRevenueAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // Revenue-eligible sales: Pending (submitted, awaiting approval),
        // Approved (signed off), Invoiced (delivered). Drafts are excluded
        // (not committed) and Cancelled excluded (didn't happen). Matches
        // the handler's revenueEligibleSales filter exactly.
        //
        // The SumAsync(s => (decimal?)s.Total.Amount) cast to nullable
        // returns null when no rows match — we coalesce to 0 (empty carts
        // are normal).
        //
        // AsNoTracking: pure read — caller never mutates these sales.
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        var sum = await query
            .Where(s => s.Status == SaleStatus.Pending
                        || s.Status == SaleStatus.Approved
                        || s.Status == SaleStatus.Invoiced)
            .SumAsync(s => (decimal?)s.Total.Amount, cancellationToken);
        return sum ?? 0m;
    }

    /// <inheritdoc />
    public async Task<decimal> SumRevenueByYearAsync(
        int year,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // Year filter uses CreatedAtUtc.Year (CreatedAtUtc is always
        // non-null and set in the Sale constructor). SubmittedAtUtc.Year
        // would also work for submitted sales, but we want a stable
        // year-bucket that doesn't depend on the submit transition
        // (which can happen long after the sale was created).
        //
        // The WHERE clause: [spec.Where] AND Status IN (Pending, Approved,
        // Invoiced) AND YEAR(CreatedAtUtc) = @year. SQL Server can use
        // the CreatedAtUtc index (SaleConfiguration adds
        // HasIndex(s => s.CreatedAtUtc)) for the year filter — the
        // YEAR() function call prevents direct index seek, but SQL Server
        // can still do an index scan with a range predicate if the
        // query optimizer rewrites it (it usually does for simple
        // year-extraction).
        //
        // AsNoTracking: pure read — caller never mutates these sales.
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        var sum = await query
            .Where(s => (s.Status == SaleStatus.Pending
                         || s.Status == SaleStatus.Approved
                         || s.Status == SaleStatus.Invoiced)
                        && s.CreatedAtUtc.Year == year)
            .SumAsync(s => (decimal?)s.Total.Amount, cancellationToken);
        return sum ?? 0m;
    }

    /// <inheritdoc />
    public async Task<List<Sale>> GetRecentSalesBySpecificationAsync(
        int count,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // Defensive: clamp count to a sane range. The handler asks for
        // 6; a future caller might pass a negative or huge number by
        // mistake. We never want to load thousands of sales with line
        // items just because the caller passed int.MaxValue.
        if (count < 0) count = 0;
        if (count > 100) count = 100;

        // SQL: SELECT TOP N ... FROM Sales s LEFT JOIN SaleLineItems li
        //      ON s.Id = li.SaleId WHERE [spec.Where]
        //      ORDER BY COALESCE(s.SubmittedAtUtc, s.CreatedAtUtc) DESC
        //
        // The OrderByDescending(s => s.SubmittedAtUtc ?? s.CreatedAtUtc)
        // matches the handler's previous in-memory
        // (s.SubmittedAtUtc ?? s.CreatedAtUtc) fallback. Drafts
        // (SubmittedAtUtc=null) sort by their CreatedAtUtc; submitted
        // sales sort by their SubmittedAtUtc.
        //
        // AsNoTracking: pure read — the recent-orders widget displays
        // these sales but never mutates them. AsNoTracking keeps the
        // change tracker lean.
        //
        // NOTE: this returns UNTRACKED entities. The caller does not
        // mutate them — the dashboard just reads them for display.
        // This is different from GetByIdAsync / GetActiveDraftForUserAsync,
        // which return tracked entities because their callers DO mutate
        // them.
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking().Include(s => s.LineItems), specification);
        return await query
            .OrderByDescending(s => s.SubmittedAtUtc ?? s.CreatedAtUtc)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}
using Ardalis.Specification;
using Ardalis.Specification.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Common.Models;
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
    // SCALAR + GROUPED-AGGREGATION IMPLEMENTATIONS
    // (Brutal Code Review v3 #23 / Round 18-C + Round 6 — see
    // ISaleRepository docs; every method here runs a single SQL
    // statement and returns only aggregated rows, never entities)
    // ==================================================================

    /// <inheritdoc />
    public async Task<List<StatusCountRow>> GetStatusCountsAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // SQL: SELECT Status, COUNT(*) FROM Sales
        //      WHERE [spec.Where] GROUP BY Status
        //
        // One round-trip replaces the former five COUNT queries (one
        // per KPI status) AND the in-memory GroupBy the handler used
        // for the status donut (which was the only place Invoiced was
        // counted). Zero-count statuses are simply absent from the
        // result — callers coalesce with 0.
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        return await query
            .GroupBy(s => s.Status)
            .Select(g => new StatusCountRow(g.Key, g.Count()))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<DailySaleStatsRow>> GetDailyStatusStatsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int bucketOffsetMinutes,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // SQL: SELECT YEAR/MONTH/DAY of COALESCE(SubmittedAtUtc,
        //      CreatedAtUtc) [+ @offset], Status, COUNT(*),
        //             SUM(Total_Amount)
        //      FROM Sales
        //      WHERE [spec.Where] AND anchor >= @from AND anchor < @to
        //      GROUP BY day-triple, Status
        //
        // DAY BUCKETING: the anchor's .Year/.Month/.Day triple is used
        // instead of .Date because the member triple translates on BOTH
        // providers (SQL Server: DATEPART/YEAR(); SQLite: strftime) —
        // while DateTime.Date has no SQLite translation. The C# side
        // reassembles the triple into a Date (see below).
        //
        // BUCKET OFFSET: the anchor is shifted by bucketOffsetMinutes
        // BEFORE the triple is taken (AddMinutes translates on both
        // providers — DATEADD on SQL Server, datetime() modifiers on
        // SQLite). 0 = UTC days; 210 = Tehran days (see the interface
        // doc for why the period chart needs Tehran alignment).
        //
        // HALF-OPEN WINDOW: anchor >= fromUtc AND anchor < toUtc —
        // matching every other windowed method on this repository.
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);

        IQueryable<Sale> windowed = query
            .Where(s => (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= fromUtc
                        && (s.SubmittedAtUtc ?? s.CreatedAtUtc) < toUtc);

        var rows = await windowed
            .GroupBy(s => new
            {
                Year = (s.SubmittedAtUtc ?? s.CreatedAtUtc).AddMinutes(bucketOffsetMinutes).Year,
                Month = (s.SubmittedAtUtc ?? s.CreatedAtUtc).AddMinutes(bucketOffsetMinutes).Month,
                Day = (s.SubmittedAtUtc ?? s.CreatedAtUtc).AddMinutes(bucketOffsetMinutes).Day,
                Status = s.Status
            })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                g.Key.Day,
                g.Key.Status,
                Count = g.Count(),
                TotalAmountRaw = g.Sum(s => s.Total.Amount)
            })
            .ToListAsync(cancellationToken);

        // Reassemble the (Year, Month, Day) triple into a Date. The
        // intermediate anonymous projection keeps the SQL translation
        // provider-agnostic; this loop is a handful of rows.
        return rows
            .Select(r => new DailySaleStatsRow(
                new DateTime(r.Year, r.Month, r.Day, 0, 0, 0, DateTimeKind.Utc),
                r.Status,
                r.Count,
                r.TotalAmountRaw))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<WindowStatusStatsRow>> GetWindowStatusStatsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // SQL: SELECT Status, COUNT(*), SUM(Total_Amount)
        //      FROM Sales
        //      WHERE [spec.Where] AND anchor >= @from AND anchor < @to
        //      GROUP BY Status
        //
        // Instant-precision counterpart of GetDailyStatusStatsAsync:
        // no day bucketing at all, so non-UTC-midnight window bounds
        // (Tehran midnights) are honored exactly. ≤5 rows come back.
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        var rows = await query
            .Where(s => (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= fromUtc
                        && (s.SubmittedAtUtc ?? s.CreatedAtUtc) < toUtc)
            .GroupBy(s => s.Status)
            .Select(g => new
            {
                Status = g.Key,
                Count = g.Count(),
                TotalAmountRaw = g.Sum(s => s.Total.Amount)
            })
            .ToListAsync(cancellationToken);

        // Map to the record OUTSIDE the queryable — see the note on
        // GetTopProductsAsync.
        return rows
            .Select(r => new WindowStatusStatsRow(r.Status, r.Count, r.TotalAmountRaw))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<TopProductSaleRow>> GetTopProductsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int top,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // SQL: SELECT li.ProductName, SUM(li.Quantity),
        //             SUM(li.Quantity * li.UnitPrice_Amount)
        //      FROM Sales s JOIN SaleLineItems li ON li.SaleId = s.Id
        //      WHERE [spec.Where] AND s.Status IN (Pending, Approved,
        //            Invoiced) AND s.anchor >= @from AND s.anchor < @to
        //      GROUP BY li.ProductName
        //      ORDER BY SUM(...) DESC LIMIT @top
        //
        // GROSSTOTAL: the domain's SaleLineItem.GrossTotal is a computed
        // C# property (Quantity * UnitPrice) with no mapped column — EF
        // cannot translate the property getter, so the arithmetic is
        // expressed inline in the SUM selector. UnitPrice is a complex
        // Money property flattened to UnitPrice_Amount, the same
        // flattening SumRevenueAsync relies on for Total.Amount.
        if (top <= 0)
        {
            return new List<TopProductSaleRow>();
        }

        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        var rows = await query
            .Where(s => (s.Status == SaleStatus.Pending
                         || s.Status == SaleStatus.Approved
                         || s.Status == SaleStatus.Invoiced)
                        && (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= fromUtc
                        && (s.SubmittedAtUtc ?? s.CreatedAtUtc) < toUtc)
            .SelectMany(s => s.LineItems)
            .GroupBy(li => li.ProductName)
            .Select(g => new
            {
                ProductName = g.Key,
                QuantitySold = g.Sum(li => li.Quantity),
                TotalAmountRaw = g.Sum(li => li.Quantity * li.UnitPrice.Amount)
            })
            .OrderByDescending(r => r.TotalAmountRaw)
            .Take(top)
            .ToListAsync(cancellationToken);

        // Map to the record OUTSIDE the queryable: EF's SQLite/SQL Server
        // translators reject a positional-constructor projection whose
        // members feed a subsequent OrderBy/Take (the anonymous-type
        // projection above translates cleanly on both providers).
        return rows
            .Select(r => new TopProductSaleRow(r.ProductName, r.QuantitySold, r.TotalAmountRaw))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<CategorySaleCountRow>> GetCategorySalesCountsAsync(
        DateTime? fromUtc,
        DateTime toUtc,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // SQL: SELECT p.CategoryId, COUNT(DISTINCT s.Id)
        //      FROM Sales s
        //      JOIN SaleLineItems li ON li.SaleId = s.Id
        //      JOIN Products p ON p.Id = li.ProductId
        //      WHERE [spec.Where] AND s.Status IN (Pending, Approved,
        //            Invoiced) [AND s.anchor >= @from] AND s.anchor < @to
        //      GROUP BY p.CategoryId
        //
        // COUNT(DISTINCT SaleId) is the "a sale counts once per category
        // no matter how many of its line items belong to that category"
        // rule. A null fromUtc means ALL TIME (the default card).
        //
        // The SelectMany with a result selector keeps the parent Sale's
        // Id in scope so it can feed the DISTINCT; EF translates the
        // correlated collection + cross-DbSet Join into one SQL
        // statement (same context, so Products is queryable here).
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification)
            .Where(s => s.Status == SaleStatus.Pending
                        || s.Status == SaleStatus.Approved
                        || s.Status == SaleStatus.Invoiced);

        if (fromUtc.HasValue)
        {
            var from = fromUtc.Value;
            query = query.Where(s => (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= from);
        }

        var rows = await query
            .Where(s => (s.SubmittedAtUtc ?? s.CreatedAtUtc) < toUtc)
            .SelectMany(s => s.LineItems,
                (sale, lineItem) => new { SaleId = sale.Id, ProductId = lineItem.ProductId })
            .Join(_db.Products.AsNoTracking(),
                x => x.ProductId,
                p => p.Id,
                (x, product) => new { x.SaleId, CategoryId = product.CategoryId })
            .GroupBy(x => x.CategoryId)
            .Select(g => new
            {
                CategoryId = g.Key,
                SalesCount = g.Select(x => x.SaleId).Distinct().Count()
            })
            .ToListAsync(cancellationToken);

        // Map to the record OUTSIDE the queryable — see the note on
        // GetTopProductsAsync.
        return rows
            .Select(r => new CategorySaleCountRow(r.CategoryId, r.SalesCount))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<TopPurchaserRow>> GetTopPurchasersAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int top,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // SQL: SELECT CustomerId, CustomerName, SUM(Total_Amount)
        //      FROM Sales
        //      WHERE [spec.Where] AND Status IN (Pending, Approved,
        //            Invoiced) AND anchor >= @from AND anchor < @to
        //      GROUP BY CustomerId, CustomerName
        //      ORDER BY SUM(...) DESC LIMIT @top
        //
        // CustomerName is denormalized onto the sale (snapshot at sale
        // time), so grouping by the pair needs no join. A customer who
        // changed their display name mid-window can appear twice — same
        // behavior as the handler's former in-memory GroupBy.
        if (top <= 0)
        {
            return new List<TopPurchaserRow>();
        }

        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        var rows = await query
            .Where(s => (s.Status == SaleStatus.Pending
                         || s.Status == SaleStatus.Approved
                         || s.Status == SaleStatus.Invoiced)
                        && (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= fromUtc
                        && (s.SubmittedAtUtc ?? s.CreatedAtUtc) < toUtc)
            .GroupBy(s => new { s.CustomerId, s.CustomerName })
            .Select(g => new
            {
                g.Key.CustomerId,
                g.Key.CustomerName,
                TotalAmountRaw = g.Sum(s => s.Total.Amount)
            })
            .OrderByDescending(r => r.TotalAmountRaw)
            .Take(top)
            .ToListAsync(cancellationToken);

        // Map to the record OUTSIDE the queryable — see the note on
        // GetTopProductsAsync (constructor projections don't compose
        // with the subsequent OrderBy/Take on EF's translators).
        return rows
            .Select(r => new TopPurchaserRow(r.CustomerId, r.CustomerName, r.TotalAmountRaw))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<DateTime?> GetOldestPendingSaleAnchorAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // SQL: SELECT MIN(COALESCE(SubmittedAtUtc, CreatedAtUtc))
        //      FROM Sales WHERE [spec.Where] AND Status = Pending
        //
        // The nullable cast makes MinAsync return null for an empty
        // set (no pending sales) instead of throwing.
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        return await query
            .Where(s => s.Status == SaleStatus.Pending)
            .MinAsync(s => (DateTime?)(s.SubmittedAtUtc ?? s.CreatedAtUtc), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountDistinctPurchasersAsync(
        DateTime fromUtc,
        DateTime toUtc,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // SQL: SELECT COUNT(DISTINCT CustomerId)
        //      FROM Sales
        //      WHERE [spec.Where] AND Status IN (Pending, Approved,
        //            Invoiced) AND anchor >= @from AND anchor < @to
        var query = _evaluator.GetQuery(_db.Sales.AsNoTracking(), specification);
        return await query
            .Where(s => (s.Status == SaleStatus.Pending
                         || s.Status == SaleStatus.Approved
                         || s.Status == SaleStatus.Invoiced)
                        && (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= fromUtc
                        && (s.SubmittedAtUtc ?? s.CreatedAtUtc) < toUtc)
            .Select(s => s.CustomerId)
            .Distinct()
            .CountAsync(cancellationToken);
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
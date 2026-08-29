using TakOne.Domain.Sales.Entities;
using Ardalis.Specification;
using TakOne.Domain.Sales.Enums;

using TakOne.Application.Common.Models;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

public interface ISaleRepository
{
    Task<Sale?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a Sale with its line items eagerly. Required for any operation
    /// that needs to inspect or modify the lines.
    /// </summary>
    Task<Sale?> GetByIdWithLineItemsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the user's currently-active Draft Sale (with line items
    /// eagerly loaded), or <c>null</c> if the user has no active draft.
    ///
    /// <b>"Active draft"</b> = the most recently created Sale where:
    /// <list type="bullet">
    ///   <item><c>CustomerId == userId</c>  (the user IS the customer — self-buy)</item>
    ///   <item><c>Status == Draft</c></item>
    /// </list>
    ///
    /// Used by the "Add to cart" flow on the product-detail page
    /// (<c>CreateOrAppendSaleCommand</c>): if a draft exists, we append the
    /// new line to it; if not, we create a fresh draft and add the line.
    ///
    /// <b>CONCURRENCY NOTE:</b>
    ///   The Sale aggregate allows at most one active draft per customer at a
    ///   time, but the schema does NOT enforce this with a unique index
    ///   (because the unique constraint would have to be partial:
    ///   <c>WHERE Status = 0</c> — supported by SQL Server but not by EF Core's
    ///   model builder without raw SQL). If a second draft is somehow created
    ///   (e.g. a race between two simultaneous "Add to cart" clicks), this
    ///   method returns the most recent one and the older draft becomes
    ///   "orphaned" — it'll be cleaned up by a future maintenance script.
    ///   In practice, the EF Core transaction + the user's serial click pattern
    ///   make this a non-issue.
    /// </summary>
    Task<Sale?> GetActiveDraftForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated slice of Sales matching the given specification.
    ///
    /// The caller (a query handler) builds an <see cref="ISpecification{Sale}"/>
    /// (e.g. <c>SaleByCustomerSpecification</c>) and passes it here. The
    /// Infrastructure layer's <c>SpecificationEvaluator</c> translates the
    /// spec into a LINQ query against the <c>Sales</c> DbSet — including
    /// any <c>Where</c>, <c>OrderBy</c>, <c>Include</c> clauses declared
    /// by the specification.
    ///
    /// Pass <c>Specification&lt;Sale&gt;.Empty</c> (or a null-spec) to get
    /// an unfiltered, paginated list — typically only for admin/manager views.
    /// </summary>
    Task<PaginatedResult<Sale>> GetPaginatedBySpecificationAsync
        (
        ISpecification<Sale> specification,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Returns ALL matching sales without pagination.
    /// Line items are NOT eagerly loaded — call
    /// <see cref="GetByIdWithLineItemsAsync"/> per sale if you need them.
    /// </summary>
    Task<List<Sale>> GetAllBySpecificationAsync
        (
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default
        );

    /// <summary>
    /// Returns the user's most-recently SUBMITTED sale (with line items
    /// eagerly loaded), or <c>null</c> if the user has never submitted an
    /// order. "Submitted" here means any status <c>&gt; Draft</c> and not
    /// <c>Cancelled</c> (i.e. Pending, Approved, or Invoiced) — once an
    /// order leaves the cart state, it's a "past order" the user can repeat.
    ///
    /// Used by the "Quick Reorder" feature on the shop page: we fetch the
    /// last order's line items and re-add them to the user's current draft,
    /// clamping each quantity to the current stock + current per-group
    /// purchase limit (so a re-order doesn't bypass a limit that was
    /// tightened after the original order).
    ///
    /// Ordering: by <c>SubmittedAtUtc</c> descending (the most recent
    /// submission wins). If two submissions have the same timestamp
    /// (millisecond-level race), the higher <c>Id</c> wins as a tie-breaker
    /// — but in practice this never happens because each submission is its
    /// own transaction.
    /// </summary>
    Task<Sale?> GetLastSubmittedSaleForUserAsync
        (
        Guid userId,
        CancellationToken cancellationToken = default
        );

    Task AddAsync(Sale sale, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes a Sale. ONLY valid for Sales in Draft status — the domain
    /// Sale.Cancel() throws for Drafts, so draft disposal goes through here.
    /// The repository implementation may add a defensive check that the Sale
    /// is actually a Draft before issuing the DELETE.
    /// </summary>
    Task DeleteAsync(Sale sale, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total monetary amount consumed by the customer in the
    /// given time window — used by the salary budget feature
    /// (<c>ISalaryBudgetService</c>).
    ///
    /// Includes:
    ///   <list type="bullet">
    ///     <item>The customer's active DRAFT cart Total (if any) —
    ///         "cart reserves budget" (the draft is implicitly counted as
    ///         consumed immediately, NOT just at submit time)</item>
    ///     <item>All SUBMITTED (non-Draft, non-Cancelled) sales with
    ///         <c>SubmittedAtUtc</c> in [windowStartUtc, windowEndUtc) —
    ///         includes Pending, Approved, and Invoiced statuses (anything
    ///         that has left the cart state and hasn't been cancelled)</item>
    ///   </list>
    ///
    /// Excludes:
    ///   <list type="bullet">
    ///     <item>Cancelled sales (the cancellation refund is implicit —
    ///         the cancelled sale is simply not in the sum). "Use it or
    ///         lose it": a sale cancelled in month M+1 (that was submitted
    ///         in month M) does NOT refund to M+1's window, because the
    ///         sale is no longer in M+1's [windowStartUtc, windowEndUtc)
    ///         range — it was in M's range.</item>
    ///     <item>Draft sales that are NOT the active one — there should be
    ///         at most one active draft per customer; orphan drafts are
    ///         ignored here (they have no line items anyway, so their
    ///         Total is 0). This is a defensive measure against the
    ///         "ghost draft" bug from the pre-Step-3 codebase.</item>
    ///   </list>
    ///
    /// SUBMIT IS A BUDGET NO-OP:
    ///   When a draft is submitted, its Total moves from the "draft" bucket
    ///   to the "submitted" bucket — net change = 0. The consumed amount
    ///   does NOT change at submit time.
    ///
    /// CURRENCY NOTE:
    ///   The returned amount is a raw decimal — the caller
    ///   (<c>ISalaryBudgetService</c>) is responsible for knowing the
    ///   currency (it comes from the customer's CustomerGroup.Salary).
    ///   Currency matching always applies: products priced in a different
    ///   currency are blocked before they reach this method.
    /// </summary>
    Task<decimal> GetConsumedAmountForCustomerInWindowAsync(
        Guid customerId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken cancellationToken = default);

    // ------------------------------------------------------------------
    // SCALAR + GROUPED-AGGREGATION METHODS
    // (Brutal Code Review v3 #23 / Round 18-C, scalarized further in Round 6)
    //
    // The methods below push COUNT/SUM/GROUP-BY/TOP-N aggregation DOWN
    // to SQL instead of materializing all sales into memory. Used by
    // GetDashboardStatsQueryHandler — the original handler loaded ALL
    // sales in scope (with line items) and aggregated in-memory with
    // 350+ lines of LINQ. For 100k sales that's ~50MB per dashboard
    // refresh. These methods drop that to a few KB.
    //
    // ROUND 6: the former GetAllWithLineItemsBySpecificationAsync
    // (the full-table load) and the per-status Count methods were
    // REMOVED from the contract so no future caller can reintroduce
    // the ~50MB path: every aggregation the dashboard needs now has a
    // dedicated SQL-side method.
    //
    // Each method takes an ISpecification<Sale> so the existing Employee
    // scoping (SaleByApproverSpecification — only sales the employee
    // personally approved) is preserved. The Admin path passes
    // AllSalesSpecification (no extra filter). The SpecificationEvaluator
    // composes the spec's Where clauses with the method's own
    // (status / window / Take(N)) filter into a single SQL statement.
    //
    // DATE ANCHOR CONVENTION (all windowed methods):
    //   a sale's "anchor" is COALESCE(SubmittedAtUtc, CreatedAtUtc) —
    //   the same anchor the handler's former in-memory filters used.
    //   Windows are HALF-OPEN [fromUtc, toUtc): inclusive lower bound,
    //   exclusive upper bound. Day buckets are UTC calendar days.
    // ------------------------------------------------------------------

    /// <summary>
    /// Counts sales per status in ONE round-trip (SQL GROUP BY Status).
    /// Returns one row per status PRESENT in scope — zero-count
    /// statuses are simply absent. Used by the dashboard for the
    /// per-status KPI counts (Draft/Pending/Approved/Cancelled —
    /// plus Invoiced, which the old per-status COUNTs missed), the
    /// TotalSalesCount KPI (sum of all rows), and the status donut.
    /// </summary>
    Task<List<StatusCountRow>> GetStatusCountsAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregates sales per (calendar day, status) over the half-open window
    /// <c>[fromUtc, toUtc)</c>: one row per day × status with the sale
    /// count and the RAW <c>Total.Amount</c> sum. Single SQL GROUP BY
    /// with the day derived from
    /// <c>COALESCE(SubmittedAtUtc, CreatedAtUtc)</c> — bucketed by
    /// (Year, Month, Day) triple, which translates on both the SQL
    /// Server and SQLite providers.
    /// </summary>
    /// <remarks>
    /// ALL statuses are included (Draft and Cancelled included) — the
    /// caller filters per-KPI. One call feeds the dashboard's daily
    /// KPIs (today/yesterday/this-month/last-month windows),
    /// the weekly revenue trend, and the monthly current-year chart.
    /// <paramref name="fromUtc"/> should be the earliest anchor any
    /// consumer needs (the caller computes the minimum of its windows'
    /// starts); rows before it are simply not returned.
    /// <para>
    /// <b>BUCKET OFFSET:</b> <paramref name="bucketOffsetMinutes"/> shifts
    /// each anchor BEFORE bucketing — the group key is
    /// <c>(anchor + offsetMinutes).Year/Month/Day</c>. 0 = UTC calendar
    /// days (the default — what the fixed-anchor KPIs and the weekly
    /// trend use). TakOne passes 210 (Tehran's fixed UTC+03:30) for the
    /// period-scoped chart series: the period selector's windows are
    /// Tehran-midnight aligned, so Tehran-day buckets line up EXACTLY
    /// with the window bounds (a UTC-day bucket would bleed up to
    /// 3.5h of the adjacent period into each boundary day). The
    /// returned <see cref="DailySaleStatsRow.Date"/> is the OFFSET
    /// calendar date (a Tehran date when the offset is 210).
    /// </para>
    /// </remarks>
    Task<List<DailySaleStatsRow>> GetDailyStatusStatsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int bucketOffsetMinutes,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-status counts and RAW amount sums over the half-open INSTANT
    /// window <c>[fromUtc, toUtc)</c> — same shape as
    /// <see cref="GetStatusCountsAsync"/> but windowed, with amounts.
    /// Single SQL GROUP BY Status with an anchor range filter.
    /// </summary>
    /// <remarks>
    /// Used by the dashboard's period-scoped KPIs: unlike the day-bucket
    /// aggregation, this query keys on the raw anchor INSTANT, so windows
    /// with non-UTC-midnight bounds (the period selector's Tehran
    /// midnights) are evaluated exactly. The caller composes the four
    /// KPI numbers (orders / amount / approved / invoiced) from the ≤5
    /// returned rows.
    /// </remarks>
    Task<List<WindowStatusStatsRow>> GetWindowStatusStatsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Top products by summed line revenue over the half-open window
    /// <c>[fromUtc, toUtc)</c>, across REVENUE-ELIGIBLE sales (Pending,
    /// Approved, Invoiced). Groups the sales' line items by
    /// <c>ProductName</c>, sums <c>Quantity</c> and
    /// <c>Quantity × UnitPrice.Amount</c> (the SQL-side equivalent of
    /// the domain's computed <c>GrossTotal</c>), orders by amount
    /// descending, takes <paramref name="top"/>. Single SQL statement
    /// (JOIN Sales → SaleLineItems, GROUP BY ProductName).
    /// </summary>
    Task<List<TopProductSaleRow>> GetTopProductsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int top,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-category sale counts: for each product category, the number
    /// of DISTINCT revenue-eligible sales (Pending/Approved/Invoiced)
    /// anchored in <c>[fromUtc, toUtc)</c> whose line items include at
    /// least one product of that category. Single SQL statement (JOIN
    /// Sales → SaleLineItems → Products, GROUP BY CategoryId,
    /// COUNT(DISTINCT SaleId)).
    /// </summary>
    /// <remarks>
    /// <paramref name="fromUtc"/> is NULLABLE: null means ALL TIME (no
    /// anchor filter) — the dashboard's default top-categories card is
    /// all-time. A sale counts at most ONCE per category even with
    /// several line items in the same category.
    /// </remarks>
    Task<List<CategorySaleCountRow>> GetCategorySalesCountsAsync(
        DateTime? fromUtc,
        DateTime toUtc,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Top purchasers by summed <c>Total.Amount</c> over the half-open
    /// window <c>[fromUtc, toUtc)</c>, across non-draft non-cancelled
    /// sales (Pending/Approved/Invoiced). Groups by
    /// (CustomerId, CustomerName), orders by amount descending, takes
    /// <paramref name="top"/>. Single SQL GROUP BY.
    /// </summary>
    Task<List<TopPurchaserRow>> GetTopPurchasersAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int top,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The oldest pending sale's anchor
    /// (<c>COALESCE(SubmittedAtUtc, CreatedAtUtc)</c>) — SQL MIN over
    /// pending sales only. Null when there are no pending sales. Used
    /// by the dashboard's KPI 3 footer ("oldest pending: X hours").
    /// </summary>
    Task<DateTime?> GetOldestPendingSaleAnchorAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts DISTINCT customers with at least one non-draft,
    /// non-cancelled sale anchored in <c>[fromUtc, toUtc)</c> — SQL
    /// COUNT(DISTINCT CustomerId). Used by the dashboard's
    /// "active employees this month" KPI footer.
    /// </summary>
    Task<int> CountDistinctPurchasersAsync(
        DateTime fromUtc,
        DateTime toUtc,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sums <c>Total.Amount</c> across sales matching the spec AND
    /// <c>Status</c> in (Pending, Approved, Invoiced) — i.e. the
    /// revenue-eligible sales. Single SQL SUM with WHERE clause.
    /// Returns 0 when no rows match.
    /// </summary>
    /// <remarks>
    /// The returned amount is the RAW <c>Total.Amount</c> (in the sale's
    /// currency). The caller (<c>GetDashboardStatsQueryHandler</c>)
    /// applies the IRR→Toman ÷10 conversion AFTER the SUM — i.e. the
    /// SQL SUM is on the original currency's Amount column. This keeps
    /// the SQL simple and lets one cached query feed multiple display
    /// currencies if multi-currency is ever supported.
    /// </remarks>
    Task<decimal> SumRevenueAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sums <c>Total.Amount</c> across sales matching the spec AND
    /// <c>Status</c> in (Pending, Approved, Invoiced) AND
    /// <c>CreatedAtUtc.Year == year</c>. Single SQL SUM with WHERE
    /// clause. Returns 0 when no rows match. Used by the dashboard's
    /// 5-year revenue breakdown (5 calls, one per year) — far cheaper
    /// than materializing all sales to aggregate in-memory.
    /// </summary>
    /// <remarks>
    /// Year filter uses <c>CreatedAtUtc.Year</c> (always non-null).
    /// SQL Server can use the <c>CreatedAtUtc</c> index (added in
    /// <c>SaleConfiguration</c>) for the year filter.
    /// </remarks>
    Task<decimal> SumRevenueByYearAsync(
        int year,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Latest N sales matching the spec, WITH line items eagerly
    /// loaded, ordered by <c>SubmittedAtUtc</c> desc (with
    /// <c>CreatedAtUtc</c> as fallback for drafts). Bounded result
    /// set — does NOT load the full sales table.
    /// </summary>
    /// <remarks>
    /// Used by <c>GetDashboardStatsQueryHandler</c> for the "Recent
    /// Orders" widget (top 6) AND for currency detection (the most
    /// recent sale's <c>Total.Currency</c> is used as the display
    /// currency — matches the previous in-memory behavior of taking
    /// the first non-empty currency from the loaded sales list).
    /// SQL: <c>SELECT TOP N ... ORDER BY COALESCE(SubmittedAtUtc,
    /// CreatedAtUtc) DESC</c>. AsNoTracking — pure read.
    /// </remarks>
    Task<List<Sale>> GetRecentSalesBySpecificationAsync(
        int count,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default);
}
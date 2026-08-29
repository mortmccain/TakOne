using System.Globalization;
using Ardalis.Specification;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Dashboard.DTOs;
using TakOne.Application.Dashboard.Specifications;
using TakOne.Application.Sales.Specifications;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Dashboard.Queries.GetDashboardStats;

/// <summary>
/// Handler for <see cref="GetDashboardStatsQuery"/>. Builds the
/// <see cref="DashboardStatsDto"/> consumed by the Dashboard razor page.
///
/// DATA LOADING STRATEGY (Brutal Code Review v3 #23, Round 18-C):
///   The previous handler loaded ALL sales in scope (with line items
///   eagerly loaded) via
///   <see cref="ISaleRepository.GetAllWithLineItemsBySpecificationAsync"/>
///   and aggregated in-memory with 350+ lines of LINQ. For 100k sales
///   that's ~50MB per dashboard refresh. The new strategy pushes
///   COUNT/SUM/TOP-N aggregation DOWN to SQL via the new scalar
///   methods on <see cref="ISaleRepository"/>:
///     - 5 KPI counts (Total, Draft, Pending, Approved, Cancelled) →
///       <see cref="ISaleRepository.CountBySpecificationAsync"/> +
///       <see cref="ISaleRepository.CountByStatusAsync"/> (5 round-trips,
///       each is a single SQL COUNT(*)).
///     - TotalRevenue → <see cref="ISaleRepository.SumRevenueAsync"/>
///       (single SQL SUM with WHERE Status IN (Pending, Approved,
///       Invoiced)).
///     - 5-year revenue breakdown → 5 calls to
///       <see cref="ISaleRepository.SumRevenueByYearAsync"/> (one SUM
///       per year, server-side).
///     - Recent orders (top 6) →
///       <see cref="ISaleRepository.GetRecentSalesBySpecificationAsync"/>
///       (bounded SQL TOP 6 — does NOT load the full sales table).
///   The remaining aggregations (top products, top categories, top
///   employees, weekly trend, monthly current year, status breakdown)
///   still use the in-memory pattern on the loaded sales list. They are
///   deferred to a future round — see the "DEFERRED" note at the bottom
///   of this file for the rationale and what's needed to fully
///   scalarize them.
///
/// <b>KNOWN LIMITATION — IN-MEMORY AGGREGATION (v4 finding #06):</b>
///   For the dashboard's top-products / top-categories / top-employees /
///   weekly-trend / monthly-current-year / status-breakdown aggregations,
///   this handler loads the full set of in-scope sales + line items into
///   memory and aggregates via LINQ-to-Objects. For an admin dashboard
///   scoped to a single employee's approved sales (Option D — bounded to
///   that employee's volume), this is acceptable. For an admin-wide
///   dashboard with years of historical sales (100K+ rows), each refresh
///   loads ~50MB into the application process.
///   <para>
///   The proper enterprise fix is to add scalar projection methods on
///   <c>ISaleRepository</c> (e.g. <c>GetTopProductsByRevenueAsync</c>,
///   <c>GetWeeklyTrendAsync</c>, <c>GetStatusCountsAsync</c>) that
///   translate the aggregations into SQL <c>GROUP BY</c> queries. This
///   refactor is deferred to a dedicated PR because it requires
///   integration-test coverage (which the codebase currently lacks) to
///   catch SQL-translation semantic drift — e.g. the
///   <c>SubmittedAtUtc ?? CreatedAtUtc</c> date-coalescing pattern that
///   the in-memory LINQ handles trivially but SQL needs <c>COALESCE</c>
///   for. Doing this refactor without test coverage would risk
///   introducing silent aggregation drift, which is a worse outcome than
///   the honest performance trade-off documented here.
///   </para>
///
/// CURRENCY CONVERSION (IRR → Toman):
///   Per user spec, when the sale currency is IRR, all amounts shown in the
///   UI must be in Toman (divide IRR amount by 10). The handler performs
///   this conversion ONCE for all amounts (TotalRevenue, weekly trend,
///   top employees, recent orders, monthly employee purchase total) and
///   sets <see cref="DashboardStatsDto.DisplayCurrency"/> = "تومان" +
///   <see cref="DashboardStatsDto.IsToman"/> = true. The razor page + JS
///   then just display the (already-converted) numbers + the label.
///
/// EMPLOYEE SCOPING:
///   Per roadmap Section 12.2 (Option D), an Employee's dashboard shows
///   ONLY the sales they personally approved. We use
///   <see cref="SaleByApproverSpecification"/> for this — it filters on
///   <c>Sale.ApprovedByUserId == currentUserId</c> AND
///   <c>Sale.Status &gt;= Approved</c>. The same spec is passed to every
///   scalar method so the SQL WHERE clauses compose correctly.
///
/// REVENUE EXCLUSIONS:
///   - Drafts excluded (not yet revenue — the customer hasn't committed)
///   - Cancelled excluded (didn't happen — would understate true performance)
///   - Pending, Approved, Invoiced all INCLUDED (revenue is recognized at
///     submit time, not delivery time, per TakOne's accounting model)
/// </summary>
public sealed class GetDashboardStatsQueryHandler
{
    public static async Task<Result<DashboardStatsDto>> HandleAsync
        (
        GetDashboardStatsQuery query,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        ILogger<GetDashboardStatsQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetDashboardStats: unauthenticated call rejected.");

            return Result<DashboardStatsDto>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Role resolution. Customers are NOT allowed to call this query
        //    (they're redirected away from the dashboard at login time, but
        //    defense-in-depth: reject here too). Employees get a scoped
        //    view; Admin/Manager/ReadOnly get the company-wide view.
        //
        //    SECURITY FIX (Brutal Code Review v3 #03): roles are now read
        //    from currentUser.IsInRole — server-verified claims — NOT from
        //    query.UserRoles (which was a public mutable setter that any
        //    authenticated caller could spoof to Roles.Admin). The DTO no
        //    longer carries UserRoles or RequestedByUserId.
        //
        //    CUSTOMER-ONLY SEMANTICS: staff may hold the Customer role ON
        //    TOP of their staff role ("a manager or employee who wants to
        //    buy on their own behalf gets the Customer role added later via
        //    AssignUserRoleCommand" — CreateStaffCommandValidator). Such a
        //    user is staff first and a buyer second; rejecting them here
        //    would lock an Employee+Customer out of their own dashboard.
        //    Only callers who hold Customer and NO staff role at all are
        //    treated as pure customers.
        // ------------------------------------------------------------------
        var isCustomerOnly = currentUser.IsInRole(Roles.Customer) &&
                             !currentUser.IsInRole(Roles.Admin) &&
                             !currentUser.IsInRole(Roles.Manager) &&
                             !currentUser.IsInRole(Roles.Employee) &&
                             !currentUser.IsInRole(Roles.ReadOnly);

        if (isCustomerOnly)
        {
            logger.LogWarning
                ("GetDashboardStats: Customer role attempted to call dashboard. UserId={UserId}.",
                currentUser.UserId);

            return Result<DashboardStatsDto>.Failure("Access denied: customers do not have a dashboard.");
        }

        // An Employee's dashboard shows ONLY the sales they personally
        // approved. We treat the caller as Employee-scoped ONLY when they
        // hold the Employee role AND do NOT hold a higher-privilege staff
        // role (Admin/Manager). ReadOnly is NOT Employee-scoped — a
        // ReadOnly user sees the full company-wide dashboard (they have
        // no approval authority, but they're auditors, not sales staff).
        var isEmployee = currentUser.IsInRole(Roles.Employee) &&
                         !currentUser.IsInRole(Roles.Admin) &&
                         !currentUser.IsInRole(Roles.Manager);

        // ------------------------------------------------------------------
        // 2. Build the spec based on role scope.
        //
        //    The SAME spec is passed to every scalar method below so the
        //    Employee scope (ApprovedByUserId == currentUser.UserId AND
        //    Status >= Approved) composes correctly with the per-method
        //    WHERE clauses (status / year / TOP-N). The
        //    SpecificationEvaluator produces one IQueryable<Sale> per
        //    method, and EF Core composes the additional filters into a
        //    single SQL statement.
        // ------------------------------------------------------------------
        ISpecification<Sale> spec;

        if (isEmployee)
        {
            spec = new SaleByApproverSpecification(currentUser.UserId);
        }
        else
        {
            spec = new AllSalesSpecification();
        }

        // ------------------------------------------------------------------
        // 3. SCALAR QUERIES — push COUNT/SUM to SQL.
        //
        //    The previous handler iterated `sales.Count` and
        //    `sales.Count(s => s.Status == X)` in memory after loading the
        //    full table. For 100k sales that's a ~50MB load per refresh.
        //    These 5 SQL COUNT(*) queries drop the load to a few KB and
        //    run server-side (using the Status index added in
        //    SaleConfiguration).
        //
        //    Run them in parallel via Task.WhenAll for max throughput —
        //    each is a single-statement SQL query, and the spec is the
        //    SAME for all (no shared mutable state). EF Core's DbContext
        //    is NOT thread-safe, but each Task here uses a SEPARATE
        //    query against the same DbContext — actually, that IS unsafe
        //    for a single DbContext. So we await them SEQUENTIALLY. The
        //    five COUNT(*) calls are each fast (indexed scans) and the
        //    total round-trip time is dominated by network latency
        //    (~1-2ms each on localhost, ~10ms each on a remote DB).
        //    Sequential is correct + safe; parallel would need a
        //    DbContext-per-task refactor that's out of scope here.
        // ------------------------------------------------------------------
        var totalCount = await saleRepository.CountBySpecificationAsync(spec, cancellationToken);
        var draftCount = await saleRepository.CountByStatusAsync(SaleStatus.Draft, spec, cancellationToken);
        var pendingCount = await saleRepository.CountByStatusAsync(SaleStatus.Pending, spec, cancellationToken);
        var approvedCount = await saleRepository.CountByStatusAsync(SaleStatus.Approved, spec, cancellationToken);
        var cancelledCount = await saleRepository.CountByStatusAsync(SaleStatus.Cancelled, spec, cancellationToken);

        // ------------------------------------------------------------------
        // 4. TotalRevenue — single SQL SUM with WHERE Status IN (Pending,
        //    Approved, Invoiced). Returns the RAW Total.Amount (in the
        //    sale's currency); the IRR→Toman ÷10 conversion is applied
        //    AFTER the SUM (see ToDisplay below).
        // ------------------------------------------------------------------
        var totalRevenueRaw = await saleRepository.SumRevenueAsync(spec, cancellationToken);

        // ------------------------------------------------------------------
        // 5. Recent orders — bounded SQL TOP 6 with line items eagerly
        //    loaded. Does NOT load the full sales table. The previous
        //    handler loaded ALL submitted sales and took top 6 in memory.
        //
        //    The repo orders by COALESCE(SubmittedAtUtc, CreatedAtUtc)
        //    DESC, matching the previous in-memory fallback. Returns
        //    UNTRACKED entities — the dashboard just displays them, never
        //    mutates.
        // ------------------------------------------------------------------
        var recentSales = await saleRepository.GetRecentSalesBySpecificationAsync(6, spec, cancellationToken);

        // ------------------------------------------------------------------
        // 6. Load remaining sales WITH line items for the in-memory
        //    aggregations (top products, top categories, top employees,
        //    weekly trend, monthly current year, status breakdown).
        //
        //    DEFERRED (Brutal Code Review v3 #23): this call still loads
        //    ALL sales in scope. To fully eliminate the ~50MB load, this
        //    needs to be replaced with bounded date-range queries:
        //      - Top products/categories: load sales submitted in last 30 days
        //      - Top employees: load sales submitted in current month
        //      - Weekly trend: load sales submitted in last 14 days
        //      - Monthly current year: load sales submitted in current year
        //      - Status breakdown: 5 scalar COUNTs already done above
        //        (just compose them into the StatusBreakdown DTO — easy win)
        //    A future round should add `GetSalesWithLineItemsByDateRangeAsync`
        //    to ISaleRepository and rewrite these aggregations to use it.
        //    For now, the scalar methods above are the high-value fixes
        //    (KPI counts + TotalRevenue + yearly SUMs + recent-orders
        //    bounded slice) — the rest is left as in-memory aggregation
        //    on the loaded list per the MINIMUM viable approach allowed
        //    by the task brief.
        // ------------------------------------------------------------------
        var sales = await saleRepository.GetAllWithLineItemsBySpecificationAsync(spec, cancellationToken);

        // ------------------------------------------------------------------
        // 7. Currency conversion setup. If currency is IRR, all amounts
        //    will be divided by 10 to convert to Toman for display.
        //    Currency is derived from the loaded sales list (the first
        //    non-empty Total.Currency). When sales is empty, defaults
        //    to "IRR" (which is the only currency used in production today).
        //
        //    DEFERRED: a future round could push currency detection to a
        //    scalar `GetMostRecentCurrencyAsync(spec)` query — but the
        //    savings are small (one row) and the loaded sales list is
        //    already needed for the aggregations above.
        // ------------------------------------------------------------------
        var currentYear = DateTime.UtcNow.Year;
        var now = DateTime.UtcNow;
        var todayUtc = DateTime.UtcNow.Date;
        var thisMonthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // ROUND 4 — previous-period anchors for the KPI trend deltas:
        // yesterday (for Today's Orders) and the previous calendar month
        // (for the two monthly cards). Same UTC-day/month conventions as
        // their current-period counterparts, so the comparisons are
        // apples-to-apples.
        var yesterdayUtc = todayUtc.AddDays(-1);
        var lastMonthStartUtc = thisMonthStartUtc.AddMonths(-1); // exclusive end = thisMonthStartUtc

        var rawCurrency = sales
            .Where(s => !string.IsNullOrEmpty(s.Total.Currency))
            .Select(s => s.Total.Currency)
            .FirstOrDefault() ?? "IRR";

        var isToman = string.Equals(rawCurrency, "IRR", StringComparison.OrdinalIgnoreCase);
        var displayCurrency = isToman ? "تومان" : rawCurrency;

        // Conversion function: IRR → Toman (÷10); other currencies pass through.
        decimal ToDisplay(decimal amount) => isToman ? amount / 10m : amount;

        // Revenue-eligible sales: Pending, Approved, or Invoiced.
        // Drafts and Cancelled are excluded (see class-level comment).
        //
        // NOTE: still used by the in-memory aggregations below (top
        // products, top categories, top employees, weekly trend, monthly
        // current year). The TotalRevenue field on the DTO is now set
        // from the scalar SumRevenueAsync query (above), NOT from
        // iterating this list — see step 4.
        var revenueEligibleSales = sales
            .Where(s => s.Status == SaleStatus.Pending ||
                        s.Status == SaleStatus.Approved ||
                        s.Status == SaleStatus.Invoiced)
            .ToList();

        // Submitted sales (any non-Draft status). Used for "today's orders"
        // and "this month" counts.
        var submittedSales = sales
            .Where(s => s.Status != SaleStatus.Draft)
            .ToList();

        // ------------------------------------------------------------------
        // 8. Build the weekly revenue trend (last 7 days + previous 7 days).
        //    Each series has 7 points, aligned by day-of-week.
        //
        //    DEFERRED: this iterates revenueEligibleSales 14 times (once
        //    per day, twice — this week + last week). For 100k sales
        //    that's 1.4M iterations per refresh. A future round should
        //    add a `GetRevenueByDayAsync(startDate, endDate, spec)` that
        //    pushes the day-bucketing to SQL.
        // ------------------------------------------------------------------
        var lastWeekStart = todayUtc.AddDays(-7);
        var twoWeeksAgoStart = todayUtc.AddDays(-14);

        var thisWeekRevenue = Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var day = todayUtc.AddDays(-6 + offset);
                var dayNext = day.AddDays(1);
                var dayTotal = revenueEligibleSales
                    .Where(s => (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= day &&
                                (s.SubmittedAtUtc ?? s.CreatedAtUtc) < dayNext)
                    .Sum(s => ToDisplay(s.Total.Amount));
                return new WeeklyRevenueDto
                {
                    Date = day,
                    DayLabel = day.ToString("ddd", CultureInfo.CurrentCulture),
                    TotalAmount = dayTotal
                };
            })
            .ToList();

        var lastWeekRevenue = Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var day = twoWeeksAgoStart.AddDays(offset);
                var dayNext = day.AddDays(1);
                var dayTotal = revenueEligibleSales
                    .Where(s => (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= day &&
                                (s.SubmittedAtUtc ?? s.CreatedAtUtc) < dayNext)
                    .Sum(s => ToDisplay(s.Total.Amount));
                return new WeeklyRevenueDto
                {
                    Date = day,
                    DayLabel = day.ToString("ddd", CultureInfo.CurrentCulture),
                    TotalAmount = dayTotal
                };
            })
            .ToList();

        // ------------------------------------------------------------------
        // 9. Top products by TOTAL SALES AMOUNT (last 30 days).
        //    Sum GrossTotal.Amount per ProductName across all REVENUE-ELIGIBLE
        //    sales (Pending + Approved + Invoiced — same definition as the
        //    revenue line chart) submitted in the last 30 days. Take top 7.
        //
        //    DEFERRED: bounded to last-30-days sales, but still loads all
        //    sales first (the GetAllWithLineItemsBySpecificationAsync
        //    call above) before filtering in memory. A future round should
        //    replace the load with a bounded `GetSalesInDateRangeWithLineItemsAsync`.
        // ------------------------------------------------------------------
        var thirtyDaysAgo = todayUtc.AddDays(-30);
        var recentRevenueEligibleSales = revenueEligibleSales
            .Where(s => (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= thirtyDaysAgo)
            .ToList();

        var topProducts = recentRevenueEligibleSales
            .SelectMany(s => s.LineItems)
            .GroupBy(li => li.ProductName)
            .Select(g => new TopProductDto
            {
                ProductName = g.Key,
                QuantitySold = g.Sum(li => li.Quantity),
                // Sum GrossTotal per line, already converted IRR→Toman via
                // ToDisplay (matches the weekly trend + totals everywhere
                // else on the dashboard). GrossTotal today equals Quantity ×
                // UnitPrice (no discount/tax yet); when the domain gains
                // discount/tax logic, this will pick it up automatically.
                TotalAmount = g.Sum(li => ToDisplay(li.GrossTotal.Amount))
            })
            .OrderByDescending(p => p.TotalAmount)
            .Take(7)
            .ToList();

        // ------------------------------------------------------------------
        // 10. Top categories by NUMBER OF SALES (all-time in scope).
        //     For each revenue-eligible sale (Pending + Approved + Invoiced),
        //     count 1 per unique category that appears in its line items.
        //     Then take top 5 + "Others".
        //
        //     DEFERRED: same as top products — still iterates the loaded
        //     sales list. A future round should add a `GetCategorySalesCountsAsync(spec)`
        //     that does the JOIN + GROUP BY + COUNT in SQL.
        // ------------------------------------------------------------------
        var allProductIds = sales
            .SelectMany(s => s.LineItems)
            .Select(li => li.ProductId)
            .Distinct()
            .ToList();

        var products = allProductIds.Count > 0
            ? await productRepository.GetByIdsReadOnlyAsync(allProductIds, cancellationToken)
            : new List<Domain.Products.Entities.Product>();

        var allCategories = await categoryRepository.GetAllAsync(cancellationToken);
        var categoryNameById = allCategories
            .ToDictionary(c => c.Id, c => c.Name);

        var productIdToCategoryId = products
            .ToDictionary(p => p.Id, p => p.CategoryId);

        // For each revenue-eligible sale, find the unique set of categories
        // in its line items (via the product → category lookup).
        // (Same set as revenueEligibleSales — Pending + Approved + Invoiced.
        // See section 9 above for why Pending is now included.)
        var categorySourceSales = revenueEligibleSales;

        var categorySalesCounts = new Dictionary<Guid, int>();

        foreach (var sale in categorySourceSales)
        {
            var saleCategoryIds = new HashSet<Guid>();

            foreach (var li in sale.LineItems)
            {
                if (productIdToCategoryId.TryGetValue(li.ProductId, out var categoryId))
                {
                    saleCategoryIds.Add(categoryId);
                }
            }

            foreach (var categoryId in saleCategoryIds)
            {
                categorySalesCounts[categoryId] = categorySalesCounts.GetValueOrDefault(categoryId) + 1;
            }
        }

        // Build the top-5 + "Others" list.
        var othersLabel = CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "fa"
            ? "سایر"
            : "Others";

        var topCategories = categorySalesCounts
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => new CategorySalesCountDto
            {
                CategoryName = categoryNameById.TryGetValue(kv.Key, out var name) ? name : "—",
                SalesCount = kv.Value
            })
            .ToList();

        var othersCount = categorySalesCounts
            .OrderByDescending(kv => kv.Value)
            .Skip(5)
            .Sum(kv => kv.Value);

        if (othersCount > 0)
        {
            topCategories.Add(new CategorySalesCountDto
            {
                CategoryName = othersLabel,
                SalesCount = othersCount
            });
        }

        // ------------------------------------------------------------------
        // 11. Top employees by purchase amount this month.
        //     Group non-cancelled non-draft sales submitted this month by
        //     CustomerId, sum Total.Amount, take top 4.
        //
        //     DEFERRED: bounded to current-month sales, but still iterates
        //     the loaded `submittedSales` list. A future round should add a
        //     `GetTopEmployeesByPurchaseAsync(monthStart, monthEnd, spec, top)`
        //     that does the GROUP BY + SUM + TOP in SQL.
        // ------------------------------------------------------------------
        var thisMonthSales = submittedSales
            .Where(s => s.Status != SaleStatus.Cancelled &&
                        (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= thisMonthStartUtc)
            .ToList();

        var topEmployees = thisMonthSales
            .GroupBy(s => new { s.CustomerId, s.CustomerName })
            .Select(g => new TopEmployeeDto
            {
                FullName = g.Key.CustomerName,
                TotalAmount = g.Sum(s => ToDisplay(s.Total.Amount))
            })
            .OrderByDescending(e => e.TotalAmount)
            .Take(4)
            .ToList();

        for (var i = 0; i < topEmployees.Count; i++)
        {
            topEmployees[i].Rank = i + 1;
        }

        // ------------------------------------------------------------------
        // 12. Recent orders — last 6 submitted sales, newest first.
        //     SOURCE: the bounded `recentSales` slice from step 5 (SQL
        //     TOP 6). The repo already ordered them by SubmittedAtUtc
        //     (with CreatedAtUtc fallback) desc — we just project to DTOs.
        // ------------------------------------------------------------------
        var recentOrders = recentSales
            .Select(s =>
            {
                // CA1826: use IReadOnlyList indexer instead of LINQ FirstOrDefault()
                var lineItems = s.LineItems;
                var firstLine = lineItems.Count > 0 ? lineItems[0] : null;
                var productSummary = firstLine is not null
                    ? $"{firstLine.ProductName} × {firstLine.Quantity}"
                    : "—";

                return new RecentOrderDto
                {
                    SaleNumber = s.SaleNumber?.Value ?? "—",
                    CustomerName = s.CustomerName,
                    ProductSummary = productSummary,
                    TotalAmount = ToDisplay(s.Total.Amount),
                    Status = s.Status.ToString(),
                    SubmittedAtUtc = s.SubmittedAtUtc
                };
            })
            .ToList();

        // ------------------------------------------------------------------
        // 13. Build the final DTO.
        // ------------------------------------------------------------------

        // Oldest pending sale age (in minutes). Null when there are no
        // pending sales — the razor page hides the footer in that case.
        // Computed from the OLDEST SubmittedAtUtc (or CreatedAtUtc fallback)
        // among pending sales. Used by KPI card 3's footer.
        //
        // DEFERRED: this iterates `sales` once to find pending + min
        // SubmittedAtUtc. A future round could push this to a scalar
        // `GetOldestPendingSaleSubmittedAtAsync(spec)` query.
        int? oldestPendingAgeMinutes = null;
        var pendingSales = sales.Where(s => s.Status == SaleStatus.Pending).ToList();
        if (pendingSales.Count > 0)
        {
            var oldestSubmitted = pendingSales
                .Min(s => s.SubmittedAtUtc ?? s.CreatedAtUtc);
            oldestPendingAgeMinutes = (int)(now - oldestSubmitted).TotalMinutes;
        }

        // Distinct employees (by CustomerId) who have at least one submitted
        // non-cancelled sale this month. Used by KPI card 2's footer.
        var thisMonthActiveEmployeeCount = thisMonthSales
            .Select(s => s.CustomerId)
            .Distinct()
            .Count();

        // ------------------------------------------------------------------
        // 14. Yearly data — 5 scalar SUM queries (one per year). The
        //     previous handler iterated revenueEligibleSales 5 times in
        //     memory; now each year is a single SQL SUM with WHERE
        //     Status IN (Pending, Approved, Invoiced) AND
        //     YEAR(CreatedAtUtc) = @year. Always 5 rows, even for years
        //     with zero revenue (the SUM returns 0).
        // ------------------------------------------------------------------
        var yearlyData = new List<YearlyRevenueDto>(5);
        for (var yearOffset = 0; yearOffset < 5; yearOffset++)
        {
            var year = currentYear - 4 + yearOffset;
            var yearRevenueRaw = await saleRepository.SumRevenueByYearAsync(year, spec, cancellationToken);
            yearlyData.Add(new YearlyRevenueDto
            {
                Year = year,
                // CA1305: pass IFormatProvider so year.ToString() is locale-independent.
                YearLabel = year.ToString(CultureInfo.InvariantCulture),
                TotalAmount = ToDisplay(yearRevenueRaw)
            });
        }
        // OrderBy is for safety — the loop above already inserts in
        // ascending year order, but a future caller might extend the
        // range asymmetrically. Cheap (5 rows).
        yearlyData.Sort((a, b) => a.Year.CompareTo(b.Year));

        var dto = new DashboardStatsDto
        {
            CurrentUserName = currentUser.FullName,
            IsEmployeeScoped = isEmployee,
            Currency = rawCurrency,
            DisplayCurrency = displayCurrency,
            IsToman = isToman,

            // ── Original KPI counts — now scalar SQL COUNT(*) ─────────
            TotalSalesCount = totalCount,
            DraftSalesCount = draftCount,
            PendingSalesCount = pendingCount,
            ApprovedSalesCount = approvedCount,
            CancelledSalesCount = cancelledCount,

            // ── NEW KPI counts ─────────────────────────────────────────
            // (still in-memory on the loaded `submittedSales` list — these
            // are date-range filters that don't benefit much from being
            // scalarized independently; a future round can combine them
            // into one `GetTodaysOrdersCountAsync(spec)` query, but the
            // round-trip savings are small.)
            TodayOrdersCount = submittedSales
                .Count(s => s.Status != SaleStatus.Cancelled &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc).Date == todayUtc),

            // ── ROUND 4 — previous-period KPI values (trend deltas) ──
            // Same filters as their current-period counterparts, shifted
            // one period back. In-memory on the already-loaded list (the
            // identical cost profile as the current-period computations
            // directly above).
            YesterdayOrdersCount = submittedSales
                .Count(s => s.Status != SaleStatus.Cancelled &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc).Date == yesterdayUtc),
            LastMonthEmployeePurchaseTotal = submittedSales
                .Where(s => s.Status != SaleStatus.Cancelled &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= lastMonthStartUtc &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc) < thisMonthStartUtc)
                .Sum(s => ToDisplay(s.Total.Amount)),
            LastMonthApprovedSalesCount = submittedSales
                .Count(s => s.Status == SaleStatus.Approved &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= lastMonthStartUtc &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc) < thisMonthStartUtc),
            LastMonthInvoicedSalesCount = submittedSales
                .Count(s => s.Status == SaleStatus.Invoiced &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= lastMonthStartUtc &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc) < thisMonthStartUtc),

            ThisMonthEmployeePurchaseTotal = thisMonthSales
                .Sum(s => ToDisplay(s.Total.Amount)),
            ThisMonthApprovedSalesCount = submittedSales
                .Count(s => s.Status == SaleStatus.Approved &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= thisMonthStartUtc),
            ThisMonthInvoicedSalesCount = submittedSales
                .Count(s => s.Status == SaleStatus.Invoiced &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= thisMonthStartUtc),

            // ── NEW KPI footer data (replaces hard-coded placeholders) ─
            ThisMonthActiveEmployeeCount = thisMonthActiveEmployeeCount,
            OldestPendingSaleAgeMinutes = oldestPendingAgeMinutes,

            // ── Greeting header data ───────────────────────────────────
            // Read the Gender claim (set at login time by Login.razor).
            // Null if missing (older sessions) — the page falls back to a
            // gender-neutral greeting.
            UserGender = currentUser.Gender,

            // ── Revenue — now scalar SQL SUM ──────────────────────────
            TotalRevenue = ToDisplay(totalRevenueRaw),

            // 5-year yearly breakdown (current year + 4 prior years).
            // Always 5 rows, even if some years have zero revenue.
            // SOURCE: 5 scalar SumRevenueByYearAsync queries (above).
            YearlyData = yearlyData,

            // 12 months of current year. Always 12 rows; future months = 0.
            //
            // DEFERRED: still in-memory on revenueEligibleSales. A future
            // round should add `SumRevenueByYearAndMonthAsync(year, month,
            // spec)` and call it 12 times — same pattern as the yearly
            // scalarization above. For now, the loaded sales list is
            // already in memory (used for top-products etc), so iterating
            // it 12 times for the monthly SUMs is cheap relative to the
            // initial load. The expensive part was the load itself, which
            // is now reduced by the scalar COUNT/SUM methods above.
            CurrentYearMonthlyData = Enumerable.Range(1, 12)
                .Select(month => new MonthlyRevenueDto
                {
                    Month = month,
                    MonthLabel = new DateTime(currentYear, month, 1)
                        .ToString("MMM", CultureInfo.InvariantCulture),
                    TotalAmount = revenueEligibleSales
                        .Where(s => (s.SubmittedAtUtc?.Year ?? s.CreatedAtUtc.Year) == currentYear &&
                                    (s.SubmittedAtUtc?.Month ?? s.CreatedAtUtc.Month) == month)
                        .Sum(s => ToDisplay(s.Total.Amount))
                })
                .OrderBy(m => m.Month)
                .ToList(),

            // Status breakdown — omit zero-count statuses so the donut
            // doesn't render empty slices.
            //
            // NOTE: this could be built directly from the 5 scalar
            // COUNTs above (totalCount, draftCount, pendingCount,
            // approvedCount, cancelledCount) — but the Invoiced status
            // isn't counted separately (the dashboard only shows the 5
            // primary statuses). The current GroupBy includes Invoiced
            // (which is the 6th SaleStatus value). To preserve that,
            // we'd need a 6th scalar COUNT for Invoiced. Leaving the
            // in-memory GroupBy intact for now — DEFERRED to a future
            // round that adds CountByStatusAsync(Invoiced, spec).
            StatusBreakdown = sales
                .GroupBy(s => s.Status)
                .Select(g => new StatusCountDto
                {
                    Status = g.Key.ToString(),
                    Count = g.Count()
                })
                .OrderByDescending(s => s.Count)
                .ToList(),

            // ── NEW chart data ────────────────────────────────────────
            ThisWeekRevenue = thisWeekRevenue,
            LastWeekRevenue = lastWeekRevenue,
            TopProducts = topProducts,
            TopCategories = topCategories,
            TopEmployees = topEmployees,
            RecentOrders = recentOrders
        };

        // ------------------------------------------------------------------
        // 15. Active customer count. This is a separate query against the
        //     user table (with a join to AspNetUserRoles for the Customer
        //     role). Done LAST because it's independent of the sales
        //     aggregation above and we don't want a failure here to nuke
        //     the entire dashboard.
        //
        // CANCELLATION PROPAGATION (v4 finding #13):
        //   The historical catch (Exception ex) swallowed
        //   OperationCanceledException — returning a fake 0 to a request
        //   the framework had already cancelled (user navigated away,
        //   request aborted). The 'when (ex is not OperationCanceledException)'
        //   filter lets cancellation propagate correctly to the host
        //   (Wolverine / ASP.NET Core), which then unwinds the request
        //   without producing a false-positive Warning log entry.
        // ------------------------------------------------------------------
        try
        {
            dto.ActiveCustomersCount = await userRepository.GetActiveCustomerCountAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Don't fail the whole dashboard if the user-count query blows
            // up. Log and surface a 0 — the KPI card will just show 0.
            logger.LogWarning
                (ex, "GetDashboardStats: failed to load active customer count. Defaulting to 0.");

            dto.ActiveCustomersCount = 0;
        }

        return Result<DashboardStatsDto>.Success(dto);
    }
}

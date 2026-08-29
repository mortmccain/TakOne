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
/// DATA LOADING STRATEGY (Round 6 — full SQL scalarization):
///   Rounds 18-C→5 progressively pushed the KPI counts and revenue sums
///   down to SQL, but one full-table load survived:
///   <c>GetAllWithLineItemsBySpecificationAsync</c> materialized EVERY
///   sale in scope (line items included — ~50MB for 100k sales) for the
///   top-products / top-categories / top-employees / weekly-trend /
///   monthly / status-breakdown aggregations. Round 6 deletes that load
///   (and the method itself) — every aggregation now has a dedicated
///   SQL-side GROUP BY on <see cref="ISaleRepository"/>:
///   <list type="bullet">
///     <item>Status KPI counts + status donut + TotalSalesCount →
///       <see cref="ISaleRepository.GetStatusCountsAsync"/> (ONE GROUP BY
///       replaces five COUNTs AND the donut's in-memory GroupBy — and now
///       counts Invoiced, which the per-status COUNTs missed).</item>
///     <item>Daily KPIs (today / yesterday / this-month / last-month) +
///       weekly revenue trend + monthly current-year chart →
///       <see cref="ISaleRepository.GetDailyStatusStatsAsync"/> (day ×
///       status GROUP BY; the handler slices the small row set per
///       window).</item>
///     <item>Period-scoped KPIs (the Round-5 selector) →
///       <see cref="ISaleRepository.GetWindowStatusStatsAsync"/> (instant-
///       precision status GROUP BY per window — exact at the Tehran-
///       midnight bounds the selector produces).</item>
///     <item>Top products → <see cref="ISaleRepository.GetTopProductsAsync"/>
///       (JOIN line items, GROUP BY ProductName).</item>
///     <item>Top categories →
///       <see cref="ISaleRepository.GetCategorySalesCountsAsync"/> (JOIN
///       line items → products, COUNT(DISTINCT sale) per category).</item>
///     <item>Top employees →
///       <see cref="ISaleRepository.GetTopPurchasersAsync"/> (GROUP BY
///       customer).</item>
///     <item>Oldest pending age →
///       <see cref="ISaleRepository.GetOldestPendingSaleAnchorAsync"/> (MIN).</item>
///     <item>Active-employees-this-month footer →
///       <see cref="ISaleRepository.CountDistinctPurchasersAsync"/>
///       (COUNT DISTINCT).</item>
///   </list>
///   Total traffic per refresh: ~15 single-statement queries returning
///   aggregated rows (a few KB), regardless of table size.
///
/// PERIOD SELECTOR (Round 5, chart-driven in Round 6):
///   When the query carries a FromUtc window, the three flow-type KPI
///   cards re-anchor to [FromUtc, ToUtc) (vs the equal-length preceding
///   window), and — new in Round 6 — the weekly revenue chart, the
///   top-products card, the top-categories card, and the top-employees
///   card ALL re-anchor to the same window. The chart series are bucketed
///   on TEHRAN days (bucketOffsetMinutes = 210): the selector's windows
///   are Tehran-midnight aligned (see Dashboard.razor's ToUtcInstant),
///   so Tehran-day buckets line up with the window bounds exactly.
///
/// CURRENCY CONVERSION (IRR → Toman):
///   Per user spec, when the sale currency is IRR, all amounts shown in
///   the UI must be in Toman (divide IRR amount by 10). The handler
///   performs this conversion ONCE for all amounts (TotalRevenue, weekly
///   trend, top employees, recent orders, monthly employee purchase
///   total) and sets <see cref="DashboardStatsDto.DisplayCurrency"/> =
///   "تومان" + <see cref="DashboardStatsDto.IsToman"/> = true. Every SQL
///   aggregation above runs on the RAW currency amounts; ToDisplay is
///   applied to the aggregated rows only.
///
/// EMPLOYEE SCOPING:
///   Per roadmap Section 12.2 (Option D), an Employee's dashboard shows
///   ONLY the sales they personally approved. We use
///   <see cref="SaleByApproverSpecification"/> for this — it filters on
///   <c>Sale.ApprovedByUserId == currentUserId</c> AND
///   <c>Sale.Status &gt;= Approved</c>. The same spec is passed to every
///   aggregation method so the SQL WHERE clauses compose correctly.
///
/// REVENUE EXCLUSIONS:
///   - Drafts excluded (not yet revenue — the customer hasn't committed)
///   - Cancelled excluded (didn't happen — would understate true performance)
///   - Pending, Approved, Invoiced all INCLUDED (revenue is recognized at
///     submit time, not delivery time, per TakOne's accounting model)
/// </summary>
public sealed class GetDashboardStatsQueryHandler
{
    /// <summary>Tehran's fixed UTC offset (Iran abolished DST in 2022).</summary>
    private static readonly TimeSpan TehranUtcOffset = TimeSpan.FromHours(3.5);

    public static async Task<Result<DashboardStatsDto>> HandleAsync
        (
        GetDashboardStatsQuery query,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
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
        //    The SAME spec is passed to EVERY aggregation method below so
        //    the Employee scope (ApprovedByUserId == currentUser.UserId
        //    AND Status >= Approved) composes correctly with each method's
        //    own WHERE clauses. The SpecificationEvaluator produces one
        //    IQueryable<Sale> per method, and EF Core composes the
        //    additional filters into a single SQL statement.
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
        // 3. Status counts — ONE SQL GROUP BY Status feeds the five status
        //    KPI counts, the TotalSalesCount KPI, AND the status donut
        //    (which now also sees Invoiced). Zero-count statuses are simply
        //    absent from the rows; GetValueOrDefault coalesces them to 0.
        // ------------------------------------------------------------------
        var statusCounts = await saleRepository.GetStatusCountsAsync(spec, cancellationToken);
        var statusCountByStatus = statusCounts.ToDictionary(r => r.Status, r => r.Count);

        var totalCount = statusCounts.Count == 0 ? 0 : statusCounts.Sum(r => r.Count);
        var draftCount = statusCountByStatus.GetValueOrDefault(SaleStatus.Draft);
        var pendingCount = statusCountByStatus.GetValueOrDefault(SaleStatus.Pending);
        var approvedCount = statusCountByStatus.GetValueOrDefault(SaleStatus.Approved);
        var cancelledCount = statusCountByStatus.GetValueOrDefault(SaleStatus.Cancelled);

        // ------------------------------------------------------------------
        // 4. TotalRevenue — single SQL SUM with WHERE Status IN (Pending,
        //    Approved, Invoiced). Returns the RAW Total.Amount (in the
        //    sale's currency); the IRR→Toman ÷10 conversion is applied
        //    AFTER the SUM (see ToDisplay below).
        // ------------------------------------------------------------------
        var totalRevenueRaw = await saleRepository.SumRevenueAsync(spec, cancellationToken);

        // ------------------------------------------------------------------
        // 5. Recent orders — bounded SQL TOP 6 with line items eagerly
        //    loaded. Does NOT load the full sales table. The repo orders
        //    by COALESCE(SubmittedAtUtc, CreatedAtUtc) DESC, matching the
        //    previous in-memory fallback. Returns UNTRACKED entities —
        //    the dashboard just displays them, never mutates.
        //
        //    This slice ALSO drives currency detection (Round 6): the
        //    first non-empty Total.Currency among the newest sales. The
        //    pre-Round-6 handler took the first currency of the full
        //    loaded list (effectively the oldest, in whatever order SQL
        //    returned); with a single production currency (documented on
        //    the DTO) the two agree, and "most recent" is the more honest
        //    answer if multi-currency ever lands.
        // ------------------------------------------------------------------
        var recentSales = await saleRepository.GetRecentSalesBySpecificationAsync(6, spec, cancellationToken);

        // ------------------------------------------------------------------
        // 6. Anchors + period window (Round 5 semantics, unchanged).
        // ------------------------------------------------------------------
        var currentYear = DateTime.UtcNow.Year;
        var now = DateTime.UtcNow;
        var todayUtc = DateTime.UtcNow.Date;
        var thisMonthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var currentYearStartUtc = new DateTime(currentYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ROUND 4 — previous-period anchors for the KPI trend deltas:
        // yesterday (for Today's Orders) and the previous calendar month
        // (for the two monthly cards). Same UTC-day/month conventions as
        // their current-period counterparts, so the comparisons are
        // apples-to-apples.
        var yesterdayUtc = todayUtc.AddDays(-1);
        var lastMonthStartUtc = thisMonthStartUtc.AddMonths(-1); // exclusive end = thisMonthStartUtc

        // ROUND 5 — PERIOD SELECTOR window. When the query carries a
        // FromUtc, the three flow-type KPI cards re-anchor to the
        // half-open interval [periodFrom, periodTo) and their deltas
        // compare against the immediately-preceding equal-length window
        // [previousFrom, periodFrom). ToUtc defaults to "now". An
        // inverted/degenerate window yields an empty interval (zero
        // KPIs) — never an exception, mirroring the sales list's
        // degenerate-range semantics.
        var isPeriodScoped = query.FromUtc.HasValue;
        DateTime? periodFromUtc = query.FromUtc;
        DateTime periodToUtc = query.ToUtc ?? now;
        DateTime? previousPeriodFromUtc = null;
        if (isPeriodScoped)
        {
            var length = periodToUtc - periodFromUtc!.Value;
            if (length > TimeSpan.Zero)
            {
                previousPeriodFromUtc = periodFromUtc.Value - length;
            }
            else
            {
                // Degenerate/inverted window: no period rows can match,
                // and the previous window is meaningless — leave
                // previousPeriodFromUtc null; both windows filter to empty.
                periodFromUtc = periodToUtc; // empty interval [x, x)
                previousPeriodFromUtc = periodToUtc;
            }
        }

        // ------------------------------------------------------------------
        // 7. Currency conversion setup. If currency is IRR, all amounts
        //    will be divided by 10 to convert to Toman for display.
        //    Currency is derived from the recent-sales slice (see step
        //    5). When there are no sales at all, defaults to "IRR" (the
        //    only currency used in production today).
        // ------------------------------------------------------------------
        var rawCurrency = recentSales
            .Where(s => !string.IsNullOrEmpty(s.Total.Currency))
            .Select(s => s.Total.Currency)
            .FirstOrDefault() ?? "IRR";

        var isToman = string.Equals(rawCurrency, "IRR", StringComparison.OrdinalIgnoreCase);
        var displayCurrency = isToman ? "تومان" : rawCurrency;

        // Conversion function: IRR → Toman (÷10); other currencies pass through.
        decimal ToDisplay(decimal amount) => isToman ? amount / 10m : amount;

        // Revenue-eligible statuses: Pending, Approved, Invoiced (see the
        // class-level REVENUE EXCLUSIONS note). Every windowed aggregation
        // below reuses this predicate.
        static bool IsRevenueStatus(SaleStatus status)
            => status is SaleStatus.Pending or SaleStatus.Approved or SaleStatus.Invoiced;

        // ------------------------------------------------------------------
        // 8. Daily stats (UTC-day buckets) — ONE SQL GROUP BY (day, status)
        //    that feeds every fixed-anchor consumer: today/yesterday
        //    counts, this-month/last-month KPIs, the weekly revenue
        //    trend, and the monthly current-year chart.
        //
        //    The window starts at the EARLIEST anchor any consumer needs:
        //    the current year's start (monthly chart), last month's start
        //    (KPI deltas — can precede year start in January), two weeks
        //    ago (weekly trend), and — when a period is active — the
        //    period/previous-period starts (the daily rows are not used
        //    for the period KPIs themselves, but widening the window is
        //    harmless). The upper bound is now (a future ToUtc is also
        //    fine — no anchor can exceed now).
        // ------------------------------------------------------------------
        var statsFromUtc = currentYearStartUtc;
        if (lastMonthStartUtc < statsFromUtc) statsFromUtc = lastMonthStartUtc;
        if (todayUtc.AddDays(-14) < statsFromUtc) statsFromUtc = todayUtc.AddDays(-14);
        if (isPeriodScoped)
        {
            if (periodFromUtc.HasValue && periodFromUtc.Value < statsFromUtc)
            {
                statsFromUtc = periodFromUtc.Value;
            }
            if (previousPeriodFromUtc.HasValue && previousPeriodFromUtc.Value < statsFromUtc)
            {
                statsFromUtc = previousPeriodFromUtc.Value;
            }
        }
        var statsToUtc = periodToUtc > now ? periodToUtc : now;

        var dailyRows = await saleRepository.GetDailyStatusStatsAsync(
            statsFromUtc, statsToUtc, bucketOffsetMinutes: 0, spec, cancellationToken);

        // Windowed helpers over the daily rows. The fixed-anchor windows
        // are all UTC-midnight aligned (or unbounded above), so DATE
        // comparisons against these bounds are exactly equivalent to the
        // instant comparisons the pre-Round-6 in-memory filters used.
        decimal SumDailyRevenue(DateTime fromUtc, DateTime? toUtc)
            => dailyRows
                .Where(r => r.Date >= fromUtc && (toUtc is null || r.Date < toUtc)
                            && IsRevenueStatus(r.Status))
                .Sum(r => r.TotalAmountRaw);

        int CountDaily(DateTime fromUtc, DateTime? toUtc, Func<SaleStatus, bool> statusPredicate)
            => dailyRows
                .Where(r => r.Date >= fromUtc && (toUtc is null || r.Date < toUtc)
                            && statusPredicate(r.Status))
                .Sum(r => r.Count);

        var tomorrowUtc = todayUtc.AddDays(1);

        // ------------------------------------------------------------------
        // 9. Weekly revenue trend (last 7 days + previous 7 days), UTC-day
        //    buckets. Each series has 7 points, aligned by day-of-week —
        //    the exact pre-Round-6 semantics, now sourced from the daily
        //    rows instead of a full in-memory scan.
        //
        //    PERIOD MODE (Round 6): the same chart re-anchors to the
        //    selected window — one point per TEHRAN day of the period vs
        //    the equal-length preceding window. Tehran buckets because
        //    the selector's bounds are Tehran midnights: the buckets align
        //    with the bounds exactly, so the chart's totals match the
        //    period KPIs computed in step 10 (a UTC-day bucket would bleed
        //    up to 3.5h of the adjacent period into each boundary day).
        // ------------------------------------------------------------------
        List<WeeklyRevenueDto> thisWeekRevenue;
        List<WeeklyRevenueDto> lastWeekRevenue;

        if (isPeriodScoped && periodFromUtc.HasValue)
        {
            // ── Period-scoped chart series (Tehran-day buckets) ─────────
            //
            // One extra SQL GROUP BY (offset = Tehran) over
            // [previousPeriodFrom, periodTo) — the row set is tiny (one
            // row per Tehran day × status).
            var tehranRows = await saleRepository.GetDailyStatusStatsAsync(
                previousPeriodFromUtc ?? periodFromUtc.Value,
                periodToUtc,
                bucketOffsetMinutes: (int)TehranUtcOffset.TotalMinutes,
                spec,
                cancellationToken);

            var tehranRevenueByDate = tehranRows
                .Where(r => IsRevenueStatus(r.Status))
                .GroupBy(r => r.Date)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.TotalAmountRaw));

            // Tehran-day range of the period: [from+offset).Date ..
            // (to+offset).Date − 1 day. The selector's bounds are Tehran
            // midnights, so these are exact period days (the last one is
            // today when ToUtc is open-ended — a partial day, as expected
            // for a trend chart).
            var periodFirstDay = (periodFromUtc.Value + TehranUtcOffset).Date;
            var periodLastDay = (periodToUtc + TehranUtcOffset).Date.AddDays(-1);
            var periodDayCount = (int)(periodLastDay - periodFirstDay).TotalDays + 1;

            if (periodDayCount <= 0)
            {
                // Degenerate/inverted window — no chart points at all.
                thisWeekRevenue = new List<WeeklyRevenueDto>();
                lastWeekRevenue = new List<WeeklyRevenueDto>();
            }
            else
            {
                // Label format: weekday names for short windows (parity
                // with the fixed 7-day chart); bare day-of-month beyond
                // 14 points so long windows stay readable.
                var dayLabelFormat = periodDayCount <= 14 ? "ddd" : "dd";

                thisWeekRevenue = Enumerable.Range(0, periodDayCount)
                    .Select(offset =>
                    {
                        var day = periodFirstDay.AddDays(offset);
                        return new WeeklyRevenueDto
                        {
                            Date = day,
                            DayLabel = day.ToString(dayLabelFormat, CultureInfo.CurrentCulture),
                            TotalAmount = ToDisplay(tehranRevenueByDate.GetValueOrDefault(day))
                        };
                    })
                    .ToList();

                // The preceding equal-length window: the immediately
                // preceding periodDayCount Tehran days. Same point count
                // → the chart's two series stay index-aligned.
                lastWeekRevenue = Enumerable.Range(0, periodDayCount)
                    .Select(offset =>
                    {
                        var day = periodFirstDay.AddDays(-periodDayCount + offset);
                        return new WeeklyRevenueDto
                        {
                            Date = day,
                            DayLabel = day.ToString(dayLabelFormat, CultureInfo.CurrentCulture),
                            TotalAmount = ToDisplay(tehranRevenueByDate.GetValueOrDefault(day))
                        };
                    })
                    .ToList();
            }
        }
        else
        {
            // ── Fixed-anchor weekly trend (UTC-day buckets) ─────────────
            thisWeekRevenue = Enumerable.Range(0, 7)
                .Select(offset =>
                {
                    var day = todayUtc.AddDays(-6 + offset);
                    return new WeeklyRevenueDto
                    {
                        Date = day,
                        DayLabel = day.ToString("ddd", CultureInfo.CurrentCulture),
                        TotalAmount = ToDisplay(SumDailyRevenue(day, day.AddDays(1)))
                    };
                })
                .ToList();

            var twoWeeksAgoStart = todayUtc.AddDays(-14);
            lastWeekRevenue = Enumerable.Range(0, 7)
                .Select(offset =>
                {
                    var day = twoWeeksAgoStart.AddDays(offset);
                    return new WeeklyRevenueDto
                    {
                        Date = day,
                        DayLabel = day.ToString("ddd", CultureInfo.CurrentCulture),
                        TotalAmount = ToDisplay(SumDailyRevenue(day, day.AddDays(1)))
                    };
                })
                .ToList();
        }

        // ------------------------------------------------------------------
        // 10. Period-scoped KPIs — instant-precision SQL GROUP BY per
        //     window. Unlike the day buckets, these honor the raw
        //     Tehran-midnight instants exactly, so the KPI numbers and
        //     the chart agree to the rial.
        // ------------------------------------------------------------------
        var periodOrdersCount = 0;
        var previousPeriodOrdersCount = 0;
        var periodEmployeePurchaseTotal = 0m;
        var previousPeriodEmployeePurchaseTotal = 0m;
        var periodApprovedSalesCount = 0;
        var periodInvoicedSalesCount = 0;
        var previousPeriodApprovedSalesCount = 0;
        var previousPeriodInvoicedSalesCount = 0;

        if (isPeriodScoped && periodFromUtc.HasValue)
        {
            var periodStats = await saleRepository.GetWindowStatusStatsAsync(
                periodFromUtc.Value, periodToUtc, spec, cancellationToken);

            periodOrdersCount = periodStats.Where(r => IsRevenueStatus(r.Status)).Sum(r => r.Count);
            periodEmployeePurchaseTotal = ToDisplay(
                periodStats.Where(r => IsRevenueStatus(r.Status)).Sum(r => r.TotalAmountRaw));
            periodApprovedSalesCount = periodStats
                .Where(r => r.Status == SaleStatus.Approved).Sum(r => r.Count);
            periodInvoicedSalesCount = periodStats
                .Where(r => r.Status == SaleStatus.Invoiced).Sum(r => r.Count);

            if (previousPeriodFromUtc.HasValue)
            {
                var previousStats = await saleRepository.GetWindowStatusStatsAsync(
                    previousPeriodFromUtc.Value, periodFromUtc.Value, spec, cancellationToken);

                previousPeriodOrdersCount = previousStats
                    .Where(r => IsRevenueStatus(r.Status)).Sum(r => r.Count);
                previousPeriodEmployeePurchaseTotal = ToDisplay(
                    previousStats.Where(r => IsRevenueStatus(r.Status)).Sum(r => r.TotalAmountRaw));
                previousPeriodApprovedSalesCount = previousStats
                    .Where(r => r.Status == SaleStatus.Approved).Sum(r => r.Count);
                previousPeriodInvoicedSalesCount = previousStats
                    .Where(r => r.Status == SaleStatus.Invoiced).Sum(r => r.Count);
            }
        }

        // ------------------------------------------------------------------
        // 11. Top products — SQL GROUP BY ProductName over the window.
        //     Fixed mode: last 30 days (the card's documented window).
        //     Period mode: the selected window.
        // ------------------------------------------------------------------
        var topProductsFromUtc = isPeriodScoped && periodFromUtc.HasValue
            ? periodFromUtc.Value
            : todayUtc.AddDays(-30);
        var topProductsToUtc = isPeriodScoped ? periodToUtc : now;

        var topProductRows = await saleRepository.GetTopProductsAsync(
            topProductsFromUtc, topProductsToUtc, top: 7, spec, cancellationToken);

        var topProducts = topProductRows
            .Select(r => new TopProductDto
            {
                ProductName = r.ProductName,
                QuantitySold = r.QuantitySold,
                // RAW sum converted once, here (matches every other
                // amount on the dashboard).
                TotalAmount = ToDisplay(r.TotalAmountRaw)
            })
            .ToList();

        // ------------------------------------------------------------------
        // 12. Top categories — SQL COUNT(DISTINCT sale) per category over
        //     the window. Fixed mode: ALL TIME (the card's documented
        //     scope). Period mode: the selected window.
        // ------------------------------------------------------------------
        var categoriesFromUtc = isPeriodScoped && periodFromUtc.HasValue
            ? periodFromUtc.Value
            : (DateTime?)null;
        var categoriesToUtc = isPeriodScoped ? periodToUtc : now;

        var categoryRows = await saleRepository.GetCategorySalesCountsAsync(
            categoriesFromUtc, categoriesToUtc, spec, cancellationToken);

        var allCategories = await categoryRepository.GetAllAsync(cancellationToken);
        var categoryNameById = allCategories
            .ToDictionary(c => c.Id, c => c.Name);

        // Build the top-5 + "Others" list.
        var othersLabel = CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "fa"
            ? "سایر"
            : "Others";

        var topCategories = categoryRows
            .OrderByDescending(r => r.SalesCount)
            .Take(5)
            .Select(r => new CategorySalesCountDto
            {
                CategoryName = categoryNameById.TryGetValue(r.CategoryId, out var name) ? name : "—",
                SalesCount = r.SalesCount
            })
            .ToList();

        var othersCount = categoryRows
            .OrderByDescending(r => r.SalesCount)
            .Skip(5)
            .Sum(r => r.SalesCount);

        if (othersCount > 0)
        {
            topCategories.Add(new CategorySalesCountDto
            {
                CategoryName = othersLabel,
                SalesCount = othersCount
            });
        }

        // ------------------------------------------------------------------
        // 13. Top employees — SQL GROUP BY customer over the window.
        //     Fixed mode: current month (the card's title says "this
        //     month"). Period mode: the selected window.
        // ------------------------------------------------------------------
        var topEmployeesFromUtc = isPeriodScoped && periodFromUtc.HasValue
            ? periodFromUtc.Value
            : thisMonthStartUtc;
        var topEmployeesToUtc = isPeriodScoped ? periodToUtc : now;

        var purchaserRows = await saleRepository.GetTopPurchasersAsync(
            topEmployeesFromUtc, topEmployeesToUtc, top: 4, spec, cancellationToken);

        var topEmployees = purchaserRows
            .Select((r, index) => new TopEmployeeDto
            {
                FullName = r.CustomerName,
                TotalAmount = ToDisplay(r.TotalAmountRaw),
                Rank = index + 1
            })
            .ToList();

        // ------------------------------------------------------------------
        // 14. Recent orders — last 6 submitted sales, newest first.
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
        // 15. Oldest pending sale age (in minutes) — SQL MIN over pending
        //     sales' anchors. Null when there are no pending sales — the
        //     razor page hides the footer in that case. Used by KPI card
        //     3's footer.
        // ------------------------------------------------------------------
        var oldestPendingAnchor = await saleRepository.GetOldestPendingSaleAnchorAsync(
            spec, cancellationToken);
        int? oldestPendingAgeMinutes = oldestPendingAnchor is { } anchor
            ? (int)(now - anchor).TotalMinutes
            : null;

        // Distinct employees (by CustomerId) who have at least one
        // submitted non-cancelled sale this month — SQL COUNT(DISTINCT).
        // Used by KPI card 2's footer. Deliberately ALWAYS the calendar
        // month, even in period mode: the field's name promises
        // this-month semantics and the period-mode KPI card swaps its
        // footer for the "vs previous period" chip anyway.
        var thisMonthActiveEmployeeCount = await saleRepository.CountDistinctPurchasersAsync(
            thisMonthStartUtc, now, spec, cancellationToken);

        // ------------------------------------------------------------------
        // 16. Yearly data — 5 scalar SUM queries (one per year). The
        //     pre-Round-6 handler iterated revenueEligibleSales 5 times in
        //     memory; each year is a single SQL SUM with WHERE
        //     Status IN (Pending, Approved, Invoiced) AND
        //     YEAR(CreatedAtUtc) = @year. Always 5 rows, even for years
        //     with zero revenue (the SUM returns 0).
        //
        //     NOTE: the yearly chart anchors on CreatedAtUtc (stable
        //     year-bucket), NOT the SubmittedAtUtc ?? CreatedAtUtc anchor
        //     every other aggregation uses — preserved from earlier
        //     rounds; changing it would silently move revenue between
        //     years for late-submitted drafts.
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

        // ------------------------------------------------------------------
        // 17. Build the final DTO.
        // ------------------------------------------------------------------
        var dto = new DashboardStatsDto
        {
            CurrentUserName = currentUser.FullName,
            IsEmployeeScoped = isEmployee,
            Currency = rawCurrency,
            DisplayCurrency = displayCurrency,
            IsToman = isToman,

            // ── Status KPI counts — ONE SQL GROUP BY ───────────────────
            TotalSalesCount = totalCount,
            DraftSalesCount = draftCount,
            PendingSalesCount = pendingCount,
            ApprovedSalesCount = approvedCount,
            CancelledSalesCount = cancelledCount,

            // ── Fixed-anchor daily KPIs — sliced from the daily rows ──
            TodayOrdersCount = CountDaily(todayUtc, tomorrowUtc, IsRevenueStatus),
            YesterdayOrdersCount = CountDaily(yesterdayUtc, todayUtc, IsRevenueStatus),
            LastMonthEmployeePurchaseTotal = ToDisplay(
                SumDailyRevenue(lastMonthStartUtc, thisMonthStartUtc)),
            LastMonthApprovedSalesCount = CountDaily(
                lastMonthStartUtc, thisMonthStartUtc, st => st == SaleStatus.Approved),
            LastMonthInvoicedSalesCount = CountDaily(
                lastMonthStartUtc, thisMonthStartUtc, st => st == SaleStatus.Invoiced),

            // ── ROUND 5 — period-scoped KPIs (instant-precision SQL) ──
            IsPeriodScoped = isPeriodScoped,
            PeriodFromUtc = isPeriodScoped ? periodFromUtc : null,
            PeriodToUtc = isPeriodScoped ? query.ToUtc : null,
            PeriodOrdersCount = periodOrdersCount,
            PreviousPeriodOrdersCount = previousPeriodOrdersCount,
            PeriodEmployeePurchaseTotal = periodEmployeePurchaseTotal,
            PreviousPeriodEmployeePurchaseTotal = previousPeriodEmployeePurchaseTotal,
            PeriodApprovedSalesCount = periodApprovedSalesCount,
            PeriodInvoicedSalesCount = periodInvoicedSalesCount,
            PreviousPeriodApprovedSalesCount = previousPeriodApprovedSalesCount,
            PreviousPeriodInvoicedSalesCount = previousPeriodInvoicedSalesCount,

            ThisMonthEmployeePurchaseTotal = ToDisplay(
                SumDailyRevenue(thisMonthStartUtc, toUtc: null)),
            ThisMonthApprovedSalesCount = CountDaily(
                thisMonthStartUtc, toUtc: null, st => st == SaleStatus.Approved),
            ThisMonthInvoicedSalesCount = CountDaily(
                thisMonthStartUtc, toUtc: null, st => st == SaleStatus.Invoiced),

            // ── NEW KPI footer data ─────────────────────────────────────
            ThisMonthActiveEmployeeCount = thisMonthActiveEmployeeCount,
            OldestPendingSaleAgeMinutes = oldestPendingAgeMinutes,

            // ── Greeting header data ────────────────────────────────────
            // Read the Gender claim (set at login time by Login.razor).
            // Null if missing (older sessions) — the page falls back to a
            // gender-neutral greeting.
            UserGender = currentUser.Gender,

            // ── Revenue — scalar SQL SUM ────────────────────────────────
            TotalRevenue = ToDisplay(totalRevenueRaw),

            // 5-year yearly breakdown (current year + 4 prior years).
            // Always 5 rows, even if some years have zero revenue.
            // SOURCE: 5 scalar SumRevenueByYearAsync queries (above).
            YearlyData = yearlyData,

            // 12 months of current year. Always 12 rows; future months = 0.
            // SOURCE: the daily rows (UTC-day buckets) — the anchor's
            // year/month is the bucket's year/month, so this is the same
            // month attribution the pre-Round-6 in-memory scan produced.
            CurrentYearMonthlyData = Enumerable.Range(1, 12)
                .Select(month => new MonthlyRevenueDto
                {
                    Month = month,
                    MonthLabel = new DateTime(currentYear, month, 1)
                        .ToString("MMM", CultureInfo.InvariantCulture),
                    TotalAmount = ToDisplay(dailyRows
                        .Where(r => r.Date.Year == currentYear
                                    && r.Date.Month == month
                                    && IsRevenueStatus(r.Status))
                        .Sum(r => r.TotalAmountRaw))
                })
                .OrderBy(m => m.Month)
                .ToList(),

            // Status breakdown — omit zero-count statuses so the donut
            // doesn't render empty slices. SOURCE: the status-count rows
            // (which include Invoiced — the 6th status the old per-status
            // COUNTs never fed the donut).
            StatusBreakdown = statusCounts
                .Select(r => new StatusCountDto
                {
                    Status = r.Status.ToString(),
                    Count = r.Count
                })
                .OrderByDescending(s => s.Count)
                .ToList(),

            // ── Chart series ────────────────────────────────────────────
            // Fixed mode: the last 7 days vs the previous 7 (UTC-day
            // buckets). Period mode (Round 6): the selected window's
            // daily buckets vs the equal-length preceding window
            // (Tehran-day buckets — see step 9).
            ThisWeekRevenue = thisWeekRevenue,
            LastWeekRevenue = lastWeekRevenue,
            TopProducts = topProducts,
            TopCategories = topCategories,
            TopEmployees = topEmployees,
            RecentOrders = recentOrders
        };

        // ------------------------------------------------------------------
        // 18. Active customer count. This is a separate query against the
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

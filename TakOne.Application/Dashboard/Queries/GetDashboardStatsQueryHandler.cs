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
/// DATA LOADING STRATEGY:
///   Loads ALL sales in scope (with line items eagerly loaded) via
///   <see cref="ISaleRepository.GetAllWithLineItemsBySpecificationAsync"/>
///   and aggregates in-memory. Single round-trip — no N+1.
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
///   <c>Sale.Status &gt;= Approved</c>.
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
        // ------------------------------------------------------------------
        var isCustomer = query.UserRoles.Contains(Roles.Customer);

        if (isCustomer)
        {
            logger.LogWarning
                ("GetDashboardStats: Customer role attempted to call dashboard. UserId={UserId}.",
                currentUser.UserId);

            return Result<DashboardStatsDto>.Failure("Access denied: customers do not have a dashboard.");
        }

        var isEmployee = query.UserRoles.Contains(Roles.Employee) &&
                         !query.UserRoles.Contains(Roles.Admin) &&
                         !query.UserRoles.Contains(Roles.Manager);

        // ------------------------------------------------------------------
        // 2. Build the spec based on role scope.
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
        // 3. Load all sales in scope WITH line items (single round-trip).
        //    Line items are needed for top-products, top-categories, and
        //    recent-order product summaries.
        // ------------------------------------------------------------------
        var sales = await saleRepository.GetAllWithLineItemsBySpecificationAsync(spec, cancellationToken);

        // ------------------------------------------------------------------
        // 4. Currency conversion setup. If currency is IRR, all amounts
        //    will be divided by 10 to convert to Toman for display.
        // ------------------------------------------------------------------
        var currentYear = DateTime.UtcNow.Year;
        var now = DateTime.UtcNow;
        var todayUtc = DateTime.UtcNow.Date;
        var thisMonthStartUtc = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

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
        // 5. Build the weekly revenue trend (last 7 days + previous 7 days).
        //    Each series has 7 points, aligned by day-of-week.
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
        // 6. Top products by sales count (last 30 days).
        //    Sum quantity per ProductName across all REVENUE-ELIGIBLE sales
        //    (Pending + Approved + Invoiced — same definition as the revenue
        //    line chart) submitted in the last 30 days. Take top 7.
        //
        //    WHY NOT Approved+Invoiced only:
        //    The previous version excluded Pending sales here, which made the
        //    bar chart silently render empty whenever a fresh install had
        //    only Pending orders (no approvals yet). The line chart and the
        //    donut already include Pending (revenueEligibleSales), so for
        //    consistency the top-products bar must too — otherwise users
        //    see "data on 2 charts, blank on 2 charts" and assume the
        //    dashboard is broken.
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
                QuantitySold = g.Sum(li => li.Quantity)
            })
            .OrderByDescending(p => p.QuantitySold)
            .Take(7)
            .ToList();

        // ------------------------------------------------------------------
        // 7. Top categories by NUMBER OF SALES (all-time in scope).
        //    For each revenue-eligible sale (Pending + Approved + Invoiced),
        //    count 1 per unique category that appears in its line items.
        //    Then take top 5 + "Others".
        //
        //    WHY NOT Approved+Invoiced only:
        //    Same consistency reason as top-products above — the previous
        //    version excluded Pending sales, which made the pie chart render
        //    empty whenever a fresh install had only Pending orders. Using
        //    revenueEligibleSales here matches the line chart and donut.
        //
        //    This requires joining line items → products → categories. We
        //    batch-load all products by Id (single round-trip) and all
        //    categories (single round-trip), then build a lookup dictionary.
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
        // See section 6 above for why Pending is now included.)
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
        // 8. Top employees by purchase amount this month.
        //    Group non-cancelled non-draft sales submitted this month by
        //    CustomerId, sum Total.Amount, take top 4.
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
        // 9. Recent orders — last 6 submitted sales, newest first.
        // ------------------------------------------------------------------
        var recentOrders = submittedSales
            .OrderByDescending(s => s.SubmittedAtUtc ?? s.CreatedAtUtc)
            .Take(6)
            .Select(s =>
            {
                var firstLine = s.LineItems.FirstOrDefault();
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
        // 10. Build the final DTO.
        // ------------------------------------------------------------------

        // Oldest pending sale age (in minutes). Null when there are no
        // pending sales — the razor page hides the footer in that case.
        // Computed from the OLDEST SubmittedAtUtc (or CreatedAtUtc fallback)
        // among pending sales. Used by KPI card 3's footer.
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

        var dto = new DashboardStatsDto
        {
            CurrentUserName = currentUser.FullName,
            IsEmployeeScoped = isEmployee,
            Currency = rawCurrency,
            DisplayCurrency = displayCurrency,
            IsToman = isToman,

            // ── Original KPI counts ────────────────────────────────────
            TotalSalesCount = sales.Count,
            DraftSalesCount = sales.Count(s => s.Status == SaleStatus.Draft),
            PendingSalesCount = sales.Count(s => s.Status == SaleStatus.Pending),
            ApprovedSalesCount = sales.Count(s => s.Status == SaleStatus.Approved),
            CancelledSalesCount = sales.Count(s => s.Status == SaleStatus.Cancelled),

            // ── NEW KPI counts ─────────────────────────────────────────
            TodayOrdersCount = submittedSales
                .Count(s => s.Status != SaleStatus.Cancelled &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc).Date == todayUtc),
            ThisMonthEmployeePurchaseTotal = thisMonthSales
                .Sum(s => ToDisplay(s.Total.Amount)),
            ThisMonthApprovedSalesCount = submittedSales
                .Count(s => s.Status == SaleStatus.Approved &&
                            (s.SubmittedAtUtc ?? s.CreatedAtUtc) >= thisMonthStartUtc),

            // ── NEW KPI footer data (replaces hard-coded placeholders) ─
            ThisMonthActiveEmployeeCount = thisMonthActiveEmployeeCount,
            OldestPendingSaleAgeMinutes = oldestPendingAgeMinutes,

            // ── Greeting header data ───────────────────────────────────
            // Read the Gender claim (set at login time by Login.razor).
            // Null if missing (older sessions) — the page falls back to a
            // gender-neutral greeting.
            UserGender = currentUser.Gender,

            // ── Revenue ───────────────────────────────────────────────
            TotalRevenue = revenueEligibleSales.Sum(s => ToDisplay(s.Total.Amount)),

            // 5-year yearly breakdown (current year + 4 prior years).
            // Always 5 rows, even if some years have zero revenue.
            YearlyData = Enumerable.Range(currentYear - 4, 5)
                .Select(year => new YearlyRevenueDto
                {
                    Year = year,
                    YearLabel = year.ToString(),
                    TotalAmount = revenueEligibleSales
                        .Where(s => s.SubmittedAtUtc?.Year == year ||
                                    s.CreatedAtUtc.Year == year)
                        .Sum(s => ToDisplay(s.Total.Amount))
                })
                .OrderBy(y => y.Year)
                .ToList(),

            // 12 months of current year. Always 12 rows; future months = 0.
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
        // 11. Active customer count. This is a separate query against the
        //     user table (with a join to AspNetUserRoles for the Customer
        //     role). Done LAST because it's independent of the sales
        //     aggregation above and we don't want a failure here to nuke
        //     the entire dashboard.
        // ------------------------------------------------------------------
        try
        {
            dto.ActiveCustomersCount = await userRepository.GetActiveCustomerCountAsync(cancellationToken);
        }
        catch (Exception ex)
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
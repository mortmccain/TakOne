namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// The single DTO returned by <c>GetDashboardStatsQuery</c>. Carries
/// everything the Dashboard razor page needs to render in one round-trip
/// — six KPI cards, three revenue charts (5-year pie, year column,
/// monthly line), a status donut, and the welcome header.
///
/// SCOPE AWARENESS (roadmap Section 12.2):
///   For Admin/Manager/ReadOnly users, all counts + sums cover ALL sales.
///   For Employee users, all counts + sums cover only the sales THEY
///   personally approved (filtered via <c>SaleByApproverSpecification</c>).
///   The handler sets <see cref="IsEmployeeScoped"/> so the page can show
///   "Your personal performance" vs "Company-wide overview" in the header.
///
/// Customers never see this DTO — they're redirected to /Products at
/// login time (roadmap Section 12.8) and the Dashboard route is gated by
/// <c>[Authorize]</c> + role check.
/// </summary>
public sealed class DashboardStatsDto
{
    // ───────────────────────────────────────────────────────────────
    // KPI COUNTS — six cards across the top of the dashboard
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Total sales in scope, all statuses. The big number on the primary
    /// KPI card.
    /// </summary>
    public int TotalSalesCount { get; set; }

    /// <summary>
    /// Sales still in <c>SaleStatus.Draft</c> — carts not yet submitted.
    /// For an Employee-scoped dashboard this is always 0 (drafts have no
    /// approver yet), but the card is still shown for visual consistency.
    /// </summary>
    public int DraftSalesCount { get; set; }

    /// <summary>
    /// Sales in <c>SaleStatus.Pending</c> — submitted, awaiting approval.
    /// For an Employee-scoped dashboard this is also 0 (pending sales
    /// have no approver yet either).
    /// </summary>
    public int PendingSalesCount { get; set; }

    /// <summary>
    /// Sales in <c>SaleStatus.Approved</c> — staff signed off, not yet
    /// invoiced. For an Employee-scoped dashboard, this is the count of
    /// sales the employee approved that are still in Approved status
    /// (i.e. not yet moved to Invoiced).
    /// </summary>
    public int ApprovedSalesCount { get; set; }

    /// <summary>
    /// Sales in <c>SaleStatus.Cancelled</c>. Includes cancellations from
    /// both Pending and Approved states.
    /// </summary>
    public int CancelledSalesCount { get; set; }

    /// <summary>
    /// Number of active users in the <c>Customer</c> role. Used for the
    /// "Active Customers" KPI card. Computed by
    /// <see cref="TakOne.Application.Common.Interfaces.IUserRepository.GetActiveCustomerCountAsync"/>
    /// (joins DomainUsers → AspNetUserRoles → AspNetRoles on the Customer
    /// role, filtered to <c>IsActive = true</c>).
    /// </summary>
    public int ActiveCustomersCount { get; set; }


    // ───────────────────────────────────────────────────────────────
    // NEW KPI COUNTS — for the redesigned Dashboard's 4 KPI cards
    // (right-to-left in RTL: today orders, monthly employee purchase,
    // pending approvals, monthly approved sales count)
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sales submitted TODAY (UTC today), any non-Draft non-Cancelled
    /// status. KPI card 1 (rightmost in RTL): "سفارشات امروز".
    /// </summary>
    public int TodayOrdersCount { get; set; }

    /// <summary>
    /// Sum of <c>Sale.Total.Amount</c> across all non-cancelled, non-draft
    /// sales submitted in the current month, in DISPLAY currency (Toman when
    /// original is IRR). KPI card 2: "مجموع خرید کارکنان در این ماه".
    /// </summary>
    public decimal ThisMonthEmployeePurchaseTotal { get; set; }

    /// <summary>
    /// Count of APPROVED sales submitted in the current month. KPI card 4
    /// (leftmost in RTL): "تعداد فروش های این ماه". Note: this counts
    /// sales that are currently in Approved status AND were submitted this
    /// month — Invoiced sales that moved past Approved are NOT counted
    /// (they're "completed", not "approved this month").
    /// </summary>
    public int ThisMonthApprovedSalesCount { get; set; }


    // ───────────────────────────────────────────────────────────────
    // NEW KPI FOOTER DATA — computed values for the footer line on each
    // KPI card. Previously these were hard-coded placeholders ("هدف ماهانه:
    // ۳۰M تومان", "قدیمی‌ترین: ۲ ساعت پیش"). Per user spec, the footers
    // must show real data.
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Number of DISTINCT employees who have at least one submitted
    /// (non-Draft, non-Cancelled) sale this month. Shown in the footer of
    /// KPI card 2 ("مجموع خرید کارکنان در این ماه") — gives context to the
    /// total amount by showing how many employees contributed to it.
    /// Replaces the previous "هدف ماهانه: ۳۰M تومان" placeholder.
    /// </summary>
    public int ThisMonthActiveEmployeeCount { get; set; }

    /// <summary>
    /// Age (in minutes) of the OLDEST pending sale in scope. Shown in the
    /// footer of KPI card 3 ("در انتظار تأیید") as "قدیمی‌ترین: X ساعت پیش"
    /// — tells the user how long the longest-waiting approval has been
    /// sitting. Null when there are no pending sales (the card shows 0 in
    /// that case, so the footer is hidden).
    /// Replaces the previous "قدیمی‌ترین: ۲ ساعت پیش" placeholder.
    /// </summary>
    public int? OldestPendingSaleAgeMinutes { get; set; }


    // ───────────────────────────────────────────────────────────────
    // GREETING HEADER — time-of-day greeting + user gender
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The gender of the current user (Male/Female). Used to pick the
    /// correct localized title ("آقای" / "خانم" in fa-IR, "Mr." / "Ms." in
    /// en-US) for the time-of-day greeting ("Good morning, Mr. Smith").
    /// Sourced from the Gender claim set at login time.
    /// </summary>
    public string? UserGender { get; set; }


    // ───────────────────────────────────────────────────────────────
    // REVENUE — total + breakdowns for the three charts
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Sum of <c>Sale.Total.Amount</c> across all non-cancelled,
    /// non-draft sales in scope. This is the prominent "Total Revenue"
    /// figure in the welcome header.
    ///
    /// Drafts excluded (not revenue yet); cancelled excluded (didn't
    /// happen). Invoiced sales ARE included — the goods were delivered,
    /// so the revenue is recognized even if invoicing happened later.
    /// </summary>
    public decimal TotalRevenue { get; set; }

    /// <summary>
    /// ISO 4217 currency code from <c>Sale.Total.Currency</c> (e.g. "IRR"
    /// for Iranian Rial). All sales use the same currency today — the
    /// handler takes the currency from the first non-empty sale and
    /// assumes the rest match. If we ever support multi-currency, this
    /// DTO will need restructuring (per-currency totals).
    /// </summary>
    public string Currency { get; set; } = "IRR";

    /// <summary>
    /// Last 5 years of revenue (including the current year). Always
    /// exactly 5 rows, ordered ascending by year. Years with zero
    /// revenue are kept (so the column chart's x-axis is continuous).
    ///
    /// Bound to:
    ///   - <c>RadzenPieSeries</c> (5-Year Revenue Distribution) — but
    ///     the series is fed <c>YearlyData.Where(y => y.TotalAmount > 0)</c>
    ///     so empty years don't get a 0% slice.
    ///   - <c>RadzenColumnSeries</c> (Revenue by Year) — all 5 rows,
    ///     so the x-axis spans the full 5-year range even for empty years.
    /// </summary>
    public List<YearlyRevenueDto> YearlyData { get; set; } = new();

    /// <summary>
    /// 12 rows (Jan → Dec) for the current year. Always exactly 12 rows,
    /// ordered ascending by month. Future months have <c>TotalAmount = 0</c>.
    /// </summary>
    public List<MonthlyRevenueDto> CurrentYearMonthlyData { get; set; } = new();

    /// <summary>
    /// One row per <c>SaleStatus</c> value present in scope. Bound to the
    /// "Sales by Status" donut. Statuses with zero sales are omitted
    /// (empty slices are visual noise).
    /// </summary>
    public List<StatusCountDto> StatusBreakdown { get; set; } = new();


    // ───────────────────────────────────────────────────────────────
    // NEW CHART DATA — for the redesigned Dashboard's 4 charts
    // (weekly line, status donut, top products bar, category pie)
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Last 7 days of revenue (this week) for the weekly revenue trend
    /// line chart. Always exactly 7 rows, ordered ascending by date.
    /// Amounts are in DISPLAY currency (Toman when original is IRR).
    /// </summary>
    public List<WeeklyRevenueDto> ThisWeekRevenue { get; set; } = new();

    /// <summary>
    /// Previous 7 days of revenue (last week) for the dashed comparison
    /// line on the weekly revenue trend chart. Always exactly 7 rows,
    /// ordered ascending by date. The chart aligns this with
    /// <see cref="ThisWeekRevenue"/> by day-of-week so the two series
    /// are visually comparable.
    /// </summary>
    public List<WeeklyRevenueDto> LastWeekRevenue { get; set; } = new();

    /// <summary>
    /// Top 7 products by total quantity sold across approved+invoiced
    /// sales in the last 30 days, for the horizontal bar chart. Ordered
    /// descending by quantity.
    /// </summary>
    public List<TopProductDto> TopProducts { get; set; } = new();

    /// <summary>
    /// Top 5 categories by NUMBER OF APPROVED SALES that contain products
    /// in that category, plus 1 "Others" slice = 6 total. For the pie
    /// chart. Ordered descending by sales count.
    /// </summary>
    public List<CategorySalesCountDto> TopCategories { get; set; } = new();

    /// <summary>
    /// Top 4 employees by total purchase amount this month, for the side
    /// widget card "کارکنان با بالاترین مبلغ خرید در این ماه". Ordered
    /// descending by amount, with Rank 1..4.
    /// </summary>
    public List<TopEmployeeDto> TopEmployees { get; set; } = new();

    /// <summary>
    /// 6 most-recent submitted sales (any non-Draft status), for the
    /// "آخرین سفارش ها" table. Ordered descending by submission date.
    /// </summary>
    public List<RecentOrderDto> RecentOrders { get; set; } = new();


    // ───────────────────────────────────────────────────────────────
    // DISPLAY CURRENCY — for IRR → Toman conversion
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The display currency label shown next to amounts in the UI
    /// (e.g. "تومان" when original is IRR, "USD" if original is USD).
    /// Set by the handler based on <see cref="Currency"/>:
    ///   - IRR → "تومان" (and amounts divided by 10)
    ///   - anything else → the original currency code (amounts unchanged)
    /// </summary>
    public string DisplayCurrency { get; set; } = "تومان";

    /// <summary>
    /// True when <see cref="Currency"/> is IRR and the handler converted
    /// all amounts to Toman (divided by 10). The razor page + JS use this
    /// to decide tooltip formatting (e.g. whether to show "M" suffix for
    /// millions).
    /// </summary>
    public bool IsToman { get; set; } = true;


    // ───────────────────────────────────────────────────────────────
    // HEADER METADATA
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the current user is an Employee and the stats are
    /// therefore scoped to only the sales they personally approved.
    /// The page uses this to switch the subtitle between
    /// "Your personal performance" and "Company-wide overview".
    /// </summary>
    public bool IsEmployeeScoped { get; set; }

    /// <summary>
    /// The display name of the user who requested the stats. Used in the
    /// "Welcome back, &lt;name&gt;" header. Sourced from
    /// <c>ICurrentUserService.FullName</c> (which itself reads the
    /// "FullName" claim set at sign-in time by Login.razor).
    /// </summary>
    public string CurrentUserName { get; set; } = string.Empty;
}
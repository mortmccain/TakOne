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
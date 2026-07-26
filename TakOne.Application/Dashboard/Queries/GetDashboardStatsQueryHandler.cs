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
/// DATA LOADING STRATEGY (v1):
///   For v1 we load ALL sales in scope via
///   <see cref="ISaleRepository.GetAllBySpecificationAsync"/> and aggregate
///   in-memory. This is fine for small datasets (≤5 years of sales — the
///   dashboard only shows the last 5 years anyway). When the dataset grows
///   past ~10K sales, we should add server-side aggregation methods to
///   <c>ISaleRepository</c> (CountByStatusAsync, SumRevenueByYearAsync, etc.)
///   and have this handler call them directly.
///
/// WHY WE DON'T USE PaginatedResult HERE:
///   The dashboard needs every sale in scope to compute totals — pagination
///   would require either N+1 queries (bad) or a single query that loads
///   everything anyway (same as <c>GetAllBySpecificationAsync</c>).
///
/// EMPLOYEE SCOPING:
///   Per roadmap Section 12.2 (Option D), an Employee's dashboard shows
///   ONLY the sales they personally approved. We use
///   <see cref="SaleByApproverSpecification"/> for this — it filters on
///   <c>Sale.ApprovedByUserId == currentUserId</c> AND
///   <c>Sale.Status &gt;= Approved</c>. The status filter excludes Drafts
///   and Pending sales (which have no approver yet).
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
        // `ISpecification<Sale>` is the Ardalis interface (from the
        // Ardalis.Specification package). Both SaleByApproverSpecification
        // and AllSalesSpecification inherit from Specification<Sale>,
        // which implements ISpecification<Sale>. We declare the variable
        // as the interface type so the if/else branches can assign either
        // concrete spec without a cast.
        //
        // WHY if/else instead of a ternary:
        //   The ternary operator's type inference looks for a COMMON type
        //   between the two branches. The compiler would pick
        //   `Specification<Sale>` (the most-derived common base), which is
        //   fine but requires either a fully-qualified name or an extra
        //   using directive. Using if/else with the variable typed as the
        //   interface makes the intent clearer and avoids the type-inference
        //   dance.
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
        // 3. Load all sales in scope. v1 in-memory aggregation — see the
        //    class-level comment for the future migration path.
        // ------------------------------------------------------------------
        var sales = await saleRepository.GetAllBySpecificationAsync(spec, cancellationToken);

        // ------------------------------------------------------------------
        // 4. Build the DTO. All aggregations are pure LINQ on `sales`.
        // ------------------------------------------------------------------
        var currentYear = DateTime.UtcNow.Year;
        var currency = sales
            .Where(s => !string.IsNullOrEmpty(s.Total.Currency))
            .Select(s => s.Total.Currency)
            .FirstOrDefault() ?? "IRR";

        // Revenue-eligible sales: Pending, Approved, or Invoiced.
        // Drafts and Cancelled are excluded (see class-level comment).
        var revenueEligibleSales = sales
            .Where(s => s.Status == SaleStatus.Pending ||
                        s.Status == SaleStatus.Approved ||
                        s.Status == SaleStatus.Invoiced)
            .ToList();

        var dto = new DashboardStatsDto
        {
            CurrentUserName = currentUser.FullName,
            IsEmployeeScoped = isEmployee,
            Currency = currency,

            // ── KPI counts ────────────────────────────────────────────
            TotalSalesCount = sales.Count,
            DraftSalesCount = sales.Count(s => s.Status == SaleStatus.Draft),
            PendingSalesCount = sales.Count(s => s.Status == SaleStatus.Pending),
            ApprovedSalesCount = sales.Count(s => s.Status == SaleStatus.Approved),
            CancelledSalesCount = sales.Count(s => s.Status == SaleStatus.Cancelled),

            // ── Revenue ───────────────────────────────────────────────
            TotalRevenue = revenueEligibleSales.Sum(s => s.Total.Amount),

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
                        .Sum(s => s.Total.Amount)
                })
                .OrderBy(y => y.Year)
                .ToList(),

            // 12 months of current year. Always 12 rows; future months = 0.
            CurrentYearMonthlyData = Enumerable.Range(1, 12)
                .Select(month => new MonthlyRevenueDto
                {
                    Month = month,
                    MonthLabel = new DateTime(currentYear, month, 1)
                        .ToString("MMM", System.Globalization.CultureInfo.InvariantCulture),
                    TotalAmount = revenueEligibleSales
                        .Where(s => (s.SubmittedAtUtc?.Year ?? s.CreatedAtUtc.Year) == currentYear &&
                                    (s.SubmittedAtUtc?.Month ?? s.CreatedAtUtc.Month) == month)
                        .Sum(s => s.Total.Amount)
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
                .ToList()
        };

        // ------------------------------------------------------------------
        // 5. Active customer count. This is a separate query against the
        //    user table (with a join to AspNetUserRoles for the Customer
        //    role). Done LAST because it's independent of the sales
        //    aggregation above and we don't want a failure here to nuke
        //    the entire dashboard.
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
                (ex,"GetDashboardStats: failed to load active customer count. Defaulting to 0.");

            dto.ActiveCustomersCount = 0;
        }

        return Result<DashboardStatsDto>.Success(dto);
    }
}
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.Specifications;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.Queries.GetCustomerShopStats;

/// <summary>
/// Handler for <see cref="GetCustomerShopStatsQuery"/>.
///
/// DATA LOADING:
///   1. Loads ALL of the caller's purchases via
///      <see cref="SaleByCustomerSpecification"/> (filters on
///      <c>CustomerId</c>, so on-behalf purchases staff made for the
///      caller are included — pre-Round 14 this used
///      <c>SaleByCreatorSpecification</c> which excluded on-behalf sales
///      and undercounted the caller's monthly/yearly totals). No status
///      filter — we need drafts too if we ever decide to count them;
///      right now we filter to submitted-only in memory.
///   2. Aggregates in memory (matches the v1 pattern in
///      <c>GetDashboardStatsQueryHandler</c>).
///   3. <c>InStockProductCount</c> is a separate single round-trip via
///      <c>IProductRepository.CountInStockAsync</c>.
///
/// REVENUE-ELIGIBLE SALES (matches Dashboard's definition):
///   Pending, Approved, or Invoiced. Drafts and Cancelled are excluded —
///   drafts aren't committed yet, cancelled didn't happen.
///
/// TIME ZONE:
///   All comparisons are in UTC (matches how <c>Sale.SubmittedAtUtc</c> is
///   stored). A "month" here is a UTC calendar month. This is consistent
///   with the rest of the app's date handling.
/// </summary>
public sealed class GetCustomerShopStatsQueryHandler
{
    public static async Task<Result<CustomerShopStatsDto>> HandleAsync
        (
        GetCustomerShopStatsQuery query,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        ILogger<GetCustomerShopStatsQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetCustomerShopStats: unauthenticated call rejected.");
            return Result<CustomerShopStatsDto>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the caller's purchases (all statuses — filter in memory).
        //    SaleByCustomerSpecification filters on CustomerId so on-behalf
        //    purchases (staff created the sale for this customer) are
        //    included. Guards against Guid.Empty already.
        // ------------------------------------------------------------------
        var spec = new SaleByCustomerSpecification(currentUser.UserId);
        var sales = await saleRepository.GetAllBySpecificationAsync(spec, cancellationToken);

        // ------------------------------------------------------------------
        // 2. Revenue-eligible: Pending, Approved, Invoiced.
        // ------------------------------------------------------------------
        var revenueEligible = sales
            .Where(s => s.Status == SaleStatus.Pending ||
                        s.Status == SaleStatus.Approved ||
                        s.Status == SaleStatus.Invoiced)
            .ToList();

        // ------------------------------------------------------------------
        // 3. Time-window aggregations (UTC).
        //    Use SubmittedAtUtc when present, else CreatedAtUtc (a sale that
        //    was just submitted may have a very recent SubmittedAtUtc; a
        //    draft has none). Matches the Dashboard handler's fallback logic.
        // ------------------------------------------------------------------
        var nowUtc = DateTime.UtcNow;
        var currentYear = nowUtc.Year;
        var currentMonth = nowUtc.Month;

        var monthlySales = revenueEligible
            .Where(s => (s.SubmittedAtUtc?.Year ?? s.CreatedAtUtc.Year) == currentYear &&
                        (s.SubmittedAtUtc?.Month ?? s.CreatedAtUtc.Month) == currentMonth)
            .ToList();

        var yearlySales = revenueEligible
            .Where(s => (s.SubmittedAtUtc?.Year ?? s.CreatedAtUtc.Year) == currentYear)
            .ToList();

        // Currency: prefer the first sale's currency; fall back to IRR
        // (matches Dashboard's convention). All sales in the system share
        // the same currency in v1, so this is deterministic.
        var currency = revenueEligible
            .Where(s => !string.IsNullOrEmpty(s.Total.Currency))
            .Select(s => s.Total.Currency)
            .FirstOrDefault() ?? "IRR";

        var monthlyTotal = new MoneyDto
        {
            Amount = monthlySales.Sum(s => s.Total.Amount),
            Currency = currency
        };
        var yearlyTotal = new MoneyDto
        {
            Amount = yearlySales.Sum(s => s.Total.Amount),
            Currency = currency
        };

        // ------------------------------------------------------------------
        // 4. In-stock product count. Single round-trip via the repo.
        //    Wrapped in try/catch so a failure here doesn't tank the whole
        //    stats card — surface 0 instead (same pattern as the Dashboard's
        //    ActiveCustomersCount fallback).
        // ------------------------------------------------------------------
        int inStockCount;
        try
        {
            inStockCount = await productRepository.CountInStockAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning
                (ex,
                "GetCustomerShopStats: failed to load in-stock product count. Defaulting to 0.");
            inStockCount = 0;
        }

        var dto = new CustomerShopStatsDto
        {
            OrdersThisMonth = monthlySales.Count,
            MonthlyTotal = monthlyTotal,
            YearlyTotal = yearlyTotal,
            InStockProductCount = inStockCount
        };

        return Result<CustomerShopStatsDto>.Success(dto);
    }
}
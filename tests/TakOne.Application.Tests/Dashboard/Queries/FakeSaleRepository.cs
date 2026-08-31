using Ardalis.Specification;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Common.Models;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;
namespace TakOne.Application.Tests.Dashboard.Queries;

/// <summary>
/// In-memory <see cref="ISaleRepository"/> double for the dashboard
/// handler tests (Round 6). Implements ONLY the aggregation/read methods
/// <see cref="GetDashboardStatsQueryHandler"/> calls; every other member
/// throws — a dashboard test that trips one of those is a bug in the
/// test, not a scenario to fake.
/// </summary>
/// <remarks>
/// <para>
/// The fake mirrors the SQL semantics the SQLite integration tests
/// (<c>DashboardAggregationIntegrationTests</c>) pin down on the REAL
/// repository — half-open instant windows, the
/// <c>COALESCE(SubmittedAtUtc, CreatedAtUtc)</c> anchor, day buckets by
/// (Year, Month, Day) with the optional offset, revenue-status filters,
/// and TOP-N ordering. The handler tests therefore exercise the handler's
/// slicing/composition logic against the same contract production runs.
/// </para>
/// <para>
/// Specs are evaluated with the stock Ardalis
/// <see cref="SpecificationEvaluator"/> over LINQ-to-Objects (the
/// dashboard specs only declare Where/OrderBy clauses, which evaluate
/// fine in memory). Every spec handed to a tracked method is recorded
/// so tests can assert scope selection the way they used to assert
/// <c>Received()</c> on NSubstitute mocks.
/// </para>
/// </remarks>
public sealed class FakeSaleRepository : ISaleRepository
{
    private readonly List<Sale> _sales = new();

    /// <summary>Specs received by any aggregation method, in call order.</summary>
    public List<ISpecification<Sale>> ReceivedSpecs { get; } = new();

    /// <summary>Total number of aggregation/read calls made (for "did not hit the repo" assertions).</summary>
    public int CallCount { get; private set; }

    public void Seed(params Sale[] sales) => _sales.AddRange(sales);
    public void Seed(IEnumerable<Sale> sales) => _sales.AddRange(sales);

    private IEnumerable<Sale> Scoped(ISpecification<Sale> specification)
    {
        CallCount++;
        ReceivedSpecs.Add(specification);
        // The stock evaluator applies Where/OrderBy over LINQ-to-Objects
        // (the dashboard specs declare only those). Returning IEnumerable
        // keeps every downstream operator plain LINQ-to-Objects, so the
        // fake can use C# constructs expression trees forbid (out var,
        // is-patterns).
        return InMemorySpecificationEvaluator.Default
            .Evaluate(_sales, specification);
    }

    private static DateTime Anchor(Sale sale) => sale.SubmittedAtUtc ?? sale.CreatedAtUtc;

    private static bool IsRevenue(Sale sale)
        => sale.Status is SaleStatus.Pending or SaleStatus.Approved or SaleStatus.Invoiced;

    // ── Aggregation methods (the dashboard contract) ─────────────────

    public Task<List<StatusCountRow>> GetStatusCountsAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var rows = Scoped(specification)
            .GroupBy(s => s.Status)
            .Select(g => new StatusCountRow(g.Key, g.Count()))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<List<DailySaleStatsRow>> GetDailyStatusStatsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int bucketOffsetMinutes,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var rows = Scoped(specification)
            .Where(s => Anchor(s) >= fromUtc && Anchor(s) < toUtc)
            .GroupBy(s => new { Date = Anchor(s).AddMinutes(bucketOffsetMinutes).Date, Status = s.Status })
            .Select(g => new DailySaleStatsRow(
                g.Key.Date,
                g.Key.Status,
                g.Count(),
                g.Sum(s => s.Total.Amount)))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<List<WindowStatusStatsRow>> GetWindowStatusStatsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var rows = Scoped(specification)
            .Where(s => Anchor(s) >= fromUtc && Anchor(s) < toUtc)
            .GroupBy(s => s.Status)
            .Select(g => new WindowStatusStatsRow(
                g.Key,
                g.Count(),
                g.Sum(s => s.Total.Amount)))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<List<TopProductSaleRow>> GetTopProductsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int top,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var rows = Scoped(specification)
            .Where(s => IsRevenue(s) && Anchor(s) >= fromUtc && Anchor(s) < toUtc)
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
            .Select(r => new TopProductSaleRow(r.ProductName, r.QuantitySold, r.TotalAmountRaw))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<List<CategorySaleCountRow>> GetCategorySalesCountsAsync(
        DateTime? fromUtc,
        DateTime toUtc,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        // The handler joins Products for CategoryId in SQL; the fake
        // takes an explicit product→category map supplied by the test.
        var rows = Scoped(specification)
            .Where(s => IsRevenue(s))
            .Where(s => Anchor(s) < toUtc)
            .Where(s => fromUtc is null || Anchor(s) >= fromUtc.Value)
            .SelectMany(s => s.LineItems.Select(li => new { SaleId = s.Id, li.ProductId }))
            .Where(x => ProductCategoryIds.ContainsKey(x.ProductId))
            .GroupBy(x => ProductCategoryIds[x.ProductId])
            .Select(g => new CategorySaleCountRow(
                g.Key,
                g.Select(x => x.SaleId).Distinct().Count()))
            .ToList();
        return Task.FromResult(rows);
    }

    /// <summary>
    /// The product-id → category-id map the category-count fake joins
    /// against (the SQL version joins the Products table; tests supply
    /// the equivalent mapping).
    /// </summary>
    public Dictionary<Guid, Guid> ProductCategoryIds { get; } = new();

    public Task<List<TopPurchaserRow>> GetTopPurchasersAsync(
        DateTime fromUtc,
        DateTime toUtc,
        int top,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var rows = Scoped(specification)
            .Where(s => IsRevenue(s) && Anchor(s) >= fromUtc && Anchor(s) < toUtc)
            .GroupBy(s => new { s.CustomerId, s.CustomerName })
            .Select(g => new
            {
                g.Key.CustomerId,
                g.Key.CustomerName,
                TotalAmountRaw = g.Sum(s => s.Total.Amount)
            })
            .OrderByDescending(r => r.TotalAmountRaw)
            .Take(top)
            .Select(r => new TopPurchaserRow(r.CustomerId, r.CustomerName, r.TotalAmountRaw))
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<DateTime?> GetOldestPendingSaleAnchorAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var anchors = Scoped(specification)
            .Where(s => s.Status == SaleStatus.Pending)
            .Select(Anchor)
            .ToList();
        return Task.FromResult(anchors.Count == 0 ? (DateTime?)null : anchors.Min());
    }

    public Task<int> CountDistinctPurchasersAsync(
        DateTime fromUtc,
        DateTime toUtc,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var count = Scoped(specification)
            .Where(s => IsRevenue(s) && Anchor(s) >= fromUtc && Anchor(s) < toUtc)
            .Select(s => s.CustomerId)
            .Distinct()
            .Count();
        return Task.FromResult(count);
    }

    public Task<decimal> SumRevenueAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var sum = Scoped(specification)
            .Where(IsRevenue)
            .Sum(s => s.Total.Amount);
        return Task.FromResult(sum);
    }

    public Task<decimal> SumRevenueByYearAsync(
        int year,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var sum = Scoped(specification)
            .Where(s => IsRevenue(s) && s.CreatedAtUtc.Year == year)
            .Sum(s => s.Total.Amount);
        return Task.FromResult(sum);
    }

    public Task<List<Sale>> GetRecentSalesBySpecificationAsync(
        int count,
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
    {
        var rows = Scoped(specification)
            .OrderByDescending(s => s.SubmittedAtUtc ?? s.CreatedAtUtc)
            .Take(count)
            .ToList();
        return Task.FromResult(rows);
    }

    // ── Everything the dashboard never calls ──────────────────────────

    public Task<Sale?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Dashboard tests never load sales by id.");

    public Task<Sale?> GetByIdWithLineItemsAsync(Guid id, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Dashboard tests never load sales by id.");

    public Task<Sale?> GetActiveDraftForUserAsync(Guid userId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Dashboard tests never load drafts.");

    public Task<PaginatedResult<Sale>> GetPaginatedBySpecificationAsync(
        ISpecification<Sale> specification,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Dashboard tests never paginate sales.");

    public Task<List<Sale>> GetAllBySpecificationAsync(
        ISpecification<Sale> specification,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Dashboard tests never load all sales.");

    public Task<Sale?> GetLastSubmittedSaleForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Dashboard tests never load last-submitted sales.");

    public Task AddAsync(Sale sale, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Dashboard tests never write sales.");

    public Task DeleteAsync(Sale sale, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Dashboard tests never delete sales.");

    public Task<decimal> GetConsumedAmountForCustomerInWindowAsync(
        Guid customerId,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Dashboard tests never compute salary budgets.");
}

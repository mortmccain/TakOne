using System.Reflection;
using FluentAssertions;
using TakOne.Application.Common.Models;
using TakOne.Application.Dashboard.Specifications;
using TakOne.Application.Sales.Specifications;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the Round 6 dashboard aggregation methods on
/// <c>SaleRepository</c> — the SQL-side GROUP BYs that replaced the
/// dashboard's former full-table load
/// (<c>GetAllWithLineItemsBySpecificationAsync</c>, deleted in Round 6).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY REAL-DB TESTS</b>: every one of these aggregations depends on
/// EF's SQL translation — the coalesced date anchor inside GROUP BY
/// keys, <c>AddMinutes</c> in the Tehran bucket offset,
/// <c>Sum(li.Quantity * li.UnitPrice.Amount)</c> over a complex Money
/// property, the cross-DbSet JOIN for category counts, and
/// COUNT(DISTINCT). None of that is provable against in-memory mocks;
/// these tests are the contract that keeps the SQL translation honest
/// (the SQLite provider shares the complex-property flattening and
/// GROUP BY/COUNT(DISTINCT) support the production SQL Server provider
/// uses for these clauses).
/// </para>
/// <para>
/// <b>SEEDING</b>: sales are built via <see cref="Sale.Create"/> +
/// domain transitions, then status/total/anchors are pinned through
/// the same test-only reflection the rest of the suite uses.
/// </para>
/// </remarks>
public class DashboardAggregationIntegrationTests
{
    private static async Task<(SaleRepository repo, ApplicationDbContext db)> CreateSeededAsync(
        params Sale[] sales)
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var repo = new SaleRepository(db);

        foreach (var sale in sales)
        {
            await repo.AddAsync(sale, CancellationToken.None);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        return (repo, db);
    }

    /// <summary>
    /// Builds a sale with the given aggregation-relevant state: one line
    /// item (or none), the status, the RAW total (the domain recomputes
    /// the total from line items — the reflection pin keeps the total
    /// independent of the line amounts so sums are easy to reason about),
    /// and the SubmittedAtUtc anchor (CreatedAtUtc is set alongside).
    /// </summary>
    private static Sale MakeSale(
        SaleStatus status = SaleStatus.Pending,
        decimal totalAmount = 0m,
        DateTime? anchorUtc = null,
        Guid? customerId = null,
        string customerName = "Alice Customer",
        (Guid ProductId, string ProductName, int Quantity, decimal UnitPrice)? line = null,
        Guid? approvedBy = null)
    {
        var sale = Sale.Create(
            customerId ?? TestValues.CustomerId,
            customerName,
            TestValues.CreatedByUserId,
            "Test Staff");

        // Submit() requires at least one line item — always add one
        // (the caller's when provided, a canonical dummy otherwise) so
        // non-draft statuses can be reached.
        var lineOrDefault = line ?? (Guid.NewGuid(), "Dummy Line", 1, 1m);
        sale.AddLineItem(lineOrDefault.ProductId, lineOrDefault.ProductName,
            lineOrDefault.Quantity, new Money(lineOrDefault.UnitPrice, TestValues.IRR));

        if (status != SaleStatus.Draft)
        {
            sale.Submit(SaleNumber.Create(1405, Random.Shared.Next(1, 1_000_000)));
        }

        if (status == SaleStatus.Approved || status == SaleStatus.Invoiced || status == SaleStatus.Cancelled)
        {
            sale.Approve(approvedBy ?? TestValues.ApprovedByUserId);
        }

        if (status == SaleStatus.Invoiced)
        {
            sale.MarkAsInvoiced(TestValues.InvoicedByUserId);
        }

        if (status == SaleStatus.Cancelled)
        {
            sale.Cancel(TestValues.CancelledByUserId, "integration test");
        }

        // Pin the aggregate-level state the aggregations read: the
        // status (Submit/Approve/... drive it, but pinning makes the
        // intent explicit and future-proof), the total, and the anchor
        // timestamps.
        typeof(Sale).GetProperty(nameof(Sale.Total))!
            .SetValue(sale, new Money(totalAmount, TestValues.IRR));
        typeof(Sale).GetProperty(nameof(Sale.Status))!.SetValue(sale, status);

        var anchor = anchorUtc ?? new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        if (status != SaleStatus.Draft)
        {
            typeof(Sale).GetProperty(nameof(Sale.SubmittedAtUtc))!.SetValue(sale, anchor);
        }
        typeof(Sale).GetField("<CreatedAtUtc>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sale, anchor);

        return sale;
    }

    // ── GetStatusCountsAsync ──────────────────────────────────────────

    [Fact]
    public async Task StatusCounts_GroupsByStatusAndCounts()
    {
        var sales = new[]
        {
            MakeSale(SaleStatus.Pending, 100m),
            MakeSale(SaleStatus.Pending, 200m),
            MakeSale(SaleStatus.Approved, 300m),
            MakeSale(SaleStatus.Invoiced, 400m),
            MakeSale(SaleStatus.Cancelled, 500m),
            MakeSale(SaleStatus.Draft, 600m),
        };
        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var rows = await repo.GetStatusCountsAsync(
                new AllSalesSpecification(), CancellationToken.None);

            rows.Should().HaveCount(5, "all five statuses are present (one row per status)");
            rows.Single(r => r.Status == SaleStatus.Pending).Count.Should().Be(2);
            rows.Single(r => r.Status == SaleStatus.Approved).Count.Should().Be(1);
            rows.Single(r => r.Status == SaleStatus.Invoiced).Count.Should().Be(1);
            rows.Single(r => r.Status == SaleStatus.Cancelled).Count.Should().Be(1);
            rows.Single(r => r.Status == SaleStatus.Draft).Count.Should().Be(1);
        }
    }

    [Fact]
    public async Task StatusCounts_RespectsApproverScope()
    {
        var approverA = TestValues.ApprovedByUserId;
        var approverB = TestValues.InvoicedByUserId;

        var byA = MakeSale(SaleStatus.Approved, 100m, approvedBy: approverA);
        var byB = MakeSale(SaleStatus.Approved, 200m, approvedBy: approverB);
        var pending = MakeSale(SaleStatus.Pending, 300m); // no approver

        var (repo, db) = await CreateSeededAsync(byA, byB, pending);
        await using (db)
        {
            var rows = await repo.GetStatusCountsAsync(
                new SaleByApproverSpecification(approverA), CancellationToken.None);

            rows.Should().ContainSingle("an Employee's dashboard sees only the sales they approved")
                .Which.Should().Match<StatusCountRow>(r => r.Status == SaleStatus.Approved && r.Count == 1);
        }
    }

    [Fact]
    public async Task StatusCounts_EmptyScope_ReturnsNoRows()
    {
        var (repo, db) = await CreateSeededAsync();
        await using (db)
        {
            var rows = await repo.GetStatusCountsAsync(
                new AllSalesSpecification(), CancellationToken.None);

            rows.Should().BeEmpty("zero-count statuses are simply absent, not zero-filled");
        }
    }

    // ── GetDailyStatusStatsAsync (UTC buckets, offset 0) ──────────────

    [Fact]
    public async Task DailyStatusStats_BucketsByUtcDayAndStatus()
    {
        // Two sales on 2026-06-15 (different statuses + amounts), one on
        // 2026-06-16, one on 2026-05-01 (outside the window).
        var sales = new[]
        {
            MakeSale(SaleStatus.Pending, 100m, new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc)),
            MakeSale(SaleStatus.Pending, 250m, new DateTime(2026, 6, 15, 22, 30, 0, DateTimeKind.Utc)),
            MakeSale(SaleStatus.Approved, 400m, new DateTime(2026, 6, 16, 1, 0, 0, DateTimeKind.Utc)),
            MakeSale(SaleStatus.Pending, 999m, new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc)),
        };
        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var rows = await repo.GetDailyStatusStatsAsync(
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                bucketOffsetMinutes: 0,
                new AllSalesSpecification(),
                CancellationToken.None);

            rows.Should().HaveCount(2, "three in-window sales collapse into two (day, status) buckets");

            var jun15Pending = rows.Single(r =>
                r.Date == new DateTime(2026, 6, 15) && r.Status == SaleStatus.Pending);
            jun15Pending.Count.Should().Be(2, "both 06-15 sales share the day bucket");
            jun15Pending.TotalAmountRaw.Should().Be(350m, "the RAW sums compose per bucket");

            rows.Should().ContainSingle(r =>
                r.Date == new DateTime(2026, 6, 16) && r.Status == SaleStatus.Approved)
                .Which.TotalAmountRaw.Should().Be(400m);

            rows.Should().NotContain(r => r.Date == new DateTime(2026, 5, 1),
                "the 05-01 sale is outside the half-open window");
        }
    }

    [Fact]
    public async Task DailyStatusStats_DraftAnchorsOnCreatedAtUtc()
    {
        // A draft has SubmittedAtUtc = null — the anchor falls back to
        // CreatedAtUtc (the COALESCE in the SQL).
        var draft = MakeSale(SaleStatus.Draft, 50m,
            new DateTime(2026, 6, 15, 23, 0, 0, DateTimeKind.Utc));

        var (repo, db) = await CreateSeededAsync(draft);
        await using (db)
        {
            var rows = await repo.GetDailyStatusStatsAsync(
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                bucketOffsetMinutes: 0,
                new AllSalesSpecification(),
                CancellationToken.None);

            rows.Should().ContainSingle()
                .Which.Should().Match<DailySaleStatsRow>(r =>
                    r.Date == new DateTime(2026, 6, 15)
                    && r.Status == SaleStatus.Draft
                    && r.Count == 1
                    && r.TotalAmountRaw == 50m);
        }
    }

    [Fact]
    public async Task DailyStatusStats_HalfOpenWindowBounds()
    {
        // from = 06-15T00:00Z, to = 06-17T00:00Z: a sale at exactly
        // from is INCLUDED; a sale at exactly to is EXCLUDED.
        var atFrom = MakeSale(SaleStatus.Pending, 10m,
            new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc));
        var justBeforeTo = MakeSale(SaleStatus.Pending, 20m,
            new DateTime(2026, 6, 16, 23, 59, 59, DateTimeKind.Utc));
        var atTo = MakeSale(SaleStatus.Pending, 30m,
            new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc));

        var (repo, db) = await CreateSeededAsync(atFrom, justBeforeTo, atTo);
        await using (db)
        {
            var rows = await repo.GetDailyStatusStatsAsync(
                new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
                bucketOffsetMinutes: 0,
                new AllSalesSpecification(),
                CancellationToken.None);

            rows.Sum(r => r.Count).Should().Be(2,
                "the sale at exactly `to` is excluded; the sale at exactly `from` is included");
        }
    }

    // ── GetDailyStatusStatsAsync (Tehran buckets, offset 210) ─────────

    [Fact]
    public async Task DailyStatusStats_TehranOffsetBucketsLateUtcIntoNextDay()
    {
        // Tehran is UTC+03:30 with no DST. A sale at 2026-06-15T23:00Z is
        // 2026-06-16T02:30 Tehran — with offset 210 it must land in the
        // 06-16 bucket; with offset 0 it stays in 06-15.
        var lateUtcSale = MakeSale(SaleStatus.Pending, 100m,
            new DateTime(2026, 6, 15, 23, 0, 0, DateTimeKind.Utc));
        var earlyUtcSale = MakeSale(SaleStatus.Pending, 200m,
            new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc)); // 13:30 Tehran, same day

        var (repo, db) = await CreateSeededAsync(lateUtcSale, earlyUtcSale);
        await using (db)
        {
            var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

            var utcRows = await repo.GetDailyStatusStatsAsync(
                from, to, bucketOffsetMinutes: 0,
                new AllSalesSpecification(), CancellationToken.None);
            var tehranRows = await repo.GetDailyStatusStatsAsync(
                from, to, bucketOffsetMinutes: 210,
                new AllSalesSpecification(), CancellationToken.None);

            utcRows.Should().HaveCount(1,
                "offset 0: both sales (10:00Z and 23:00Z) share the 06-15 UTC day → one bucket");
            utcRows.Should().ContainSingle().Which.Date.Should().Be(new DateTime(2026, 6, 15));

            tehranRows.Should().HaveCount(2, "the two sales now fall in DIFFERENT Tehran days");
            tehranRows.Should().Contain(r => r.Date == new DateTime(2026, 6, 15)
                && r.TotalAmountRaw == 200m, "10:00Z = 13:30 Tehran, still 06-15");
            tehranRows.Should().Contain(r => r.Date == new DateTime(2026, 6, 16)
                && r.TotalAmountRaw == 100m, "23:00Z = 02:30 Tehran NEXT DAY");
        }
    }

    // ── GetWindowStatusStatsAsync ─────────────────────────────────────

    [Fact]
    public async Task WindowStatusStats_InstantBoundsAndStatusSums()
    {
        // Tehran-midnight-style bounds: 2026-06-15T20:30Z. A sale at
        // 20:29Z is OUTSIDE; 20:30Z is INSIDE — instant precision, the
        // property day-bucketing cannot offer.
        var before = MakeSale(SaleStatus.Pending, 10m,
            new DateTime(2026, 6, 15, 20, 29, 0, DateTimeKind.Utc));
        var atBound = MakeSale(SaleStatus.Approved, 20m,
            new DateTime(2026, 6, 15, 20, 30, 0, DateTimeKind.Utc));
        var inside = MakeSale(SaleStatus.Approved, 30m,
            new DateTime(2026, 6, 16, 3, 0, 0, DateTimeKind.Utc));
        var cancelled = MakeSale(SaleStatus.Cancelled, 40m,
            new DateTime(2026, 6, 16, 5, 0, 0, DateTimeKind.Utc));

        var (repo, db) = await CreateSeededAsync(before, atBound, inside, cancelled);
        await using (db)
        {
            var rows = await repo.GetWindowStatusStatsAsync(
                new DateTime(2026, 6, 15, 20, 30, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
                new AllSalesSpecification(),
                CancellationToken.None);

            rows.Should().HaveCount(2, "Approved (2 sales) + Cancelled (1 sale); the pre-bound sale is excluded");
            rows.Single(r => r.Status == SaleStatus.Approved).Count.Should().Be(2);
            rows.Single(r => r.Status == SaleStatus.Approved).TotalAmountRaw.Should().Be(50m);
            rows.Single(r => r.Status == SaleStatus.Cancelled).Count.Should().Be(1);
            rows.Should().NotContain(r => r.Status == SaleStatus.Pending,
                "the only pending sale sits before the window bound");
        }
    }

    [Fact]
    public async Task WindowStatusStats_EmptyWindow_ReturnsNoRows()
    {
        var sale = MakeSale(SaleStatus.Pending, 100m,
            new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc));
        var (repo, db) = await CreateSeededAsync(sale);
        await using (db)
        {
            var rows = await repo.GetWindowStatusStatsAsync(
                new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
                new AllSalesSpecification(),
                CancellationToken.None);

            rows.Should().BeEmpty("a degenerate [x, x) window matches nothing");
        }
    }

    // ── GetTopProductsAsync ───────────────────────────────────────────

    [Fact]
    public async Task TopProducts_SumsQuantityAndLineRevenuePerProduct()
    {
        // Sale 1: 2 × "Widget" @ 100 → 200; Sale 2: 3 × "Widget" @ 100 → 300.
        // Together: Widget = 5 units, 500. "Gadget": 1 × 50 → 50.
        var widgetBuyer1 = MakeSale(SaleStatus.Pending, totalAmount: 200m,
            line: (Guid.NewGuid(), "Widget", 2, 100m));
        var widgetBuyer2 = MakeSale(SaleStatus.Approved, totalAmount: 300m,
            line: (Guid.NewGuid(), "Widget", 3, 100m));
        var gadgetBuyer = MakeSale(SaleStatus.Pending, totalAmount: 50m,
            line: (Guid.NewGuid(), "Gadget", 1, 50m));
        var draftBuyer = MakeSale(SaleStatus.Draft, totalAmount: 70m,
            line: (Guid.NewGuid(), "Draft-only", 1, 70m));
        var cancelledBuyer = MakeSale(SaleStatus.Cancelled, totalAmount: 80m,
            line: (Guid.NewGuid(), "Cancelled-only", 1, 80m));

        var (repo, db) = await CreateSeededAsync(widgetBuyer1, widgetBuyer2, gadgetBuyer, draftBuyer, cancelledBuyer);
        await using (db)
        {
            var rows = await repo.GetTopProductsAsync(
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                top: 7,
                new AllSalesSpecification(),
                CancellationToken.None);

            rows.Should().HaveCount(2, "draft and cancelled sales are not revenue-eligible");
            var widget = rows.Single(r => r.ProductName == "Widget");
            widget.QuantitySold.Should().Be(5);
            widget.TotalAmountRaw.Should().Be(500m,
                "Quantity × UnitPrice summed per line — the SQL-side GrossTotal");

            rows.First().ProductName.Should().Be("Widget", "ordered by amount descending");
        }
    }

    [Fact]
    public async Task TopProducts_TakesTopNByAmountDescending()
    {
        var sales = Enumerable.Range(1, 5).Select(i => MakeSale(
            SaleStatus.Pending,
            totalAmount: i * 10m,
            line: (Guid.NewGuid(), $"Product{i}", 1, i * 10m))).ToArray();

        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var rows = await repo.GetTopProductsAsync(
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                top: 3,
                new AllSalesSpecification(),
                CancellationToken.None);

            rows.Should().HaveCount(3);
            rows.Select(r => r.ProductName)
                .Should().ContainInOrder("Product5", "Product4", "Product3");
        }
    }

    // ── GetCategorySalesCountsAsync ───────────────────────────────────

    [Fact]
    public async Task CategorySalesCounts_CountsDistinctSalesPerCategory()
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var repo = new SaleRepository(db);

        // Two categories with one product each.
        db.Categories.Add(Category.Create("Cat A"));
        db.Categories.Add(Category.Create("Cat B"));
        await db.SaveChangesAsync(CancellationToken.None);
        var catA = db.Categories.OrderBy(c => c.Name).First(c => c.Name == "Cat A").Id;
        var catB = db.Categories.OrderBy(c => c.Name).First(c => c.Name == "Cat B").Id;

        var productA1 = Product.Create("A1", "desc", new Money(10m, TestValues.IRR), 100, catA);
        var productA2 = Product.Create("A2", "desc", new Money(10m, TestValues.IRR), 100, catA);
        var productB1 = Product.Create("B1", "desc", new Money(10m, TestValues.IRR), 100, catB);
        db.Products.AddRange(productA1, productA2, productB1);
        await db.SaveChangesAsync(CancellationToken.None);

        // Sale 1: TWO line items in Cat A + one in Cat B → counts ONCE
        // for A, once for B. (All lines are added while the sale is a
        // DRAFT — the domain forbids adding lines after submit.)
        var sale1 = Sale.Create(
            TestValues.CustomerId, "Cat Customer",
            TestValues.CreatedByUserId, "Test Staff");
        sale1.AddLineItem(productA1.Id, "A1", 1, new Money(10m, TestValues.IRR));
        sale1.AddLineItem(productA2.Id, "A2", 1, new Money(10m, TestValues.IRR));
        sale1.AddLineItem(productB1.Id, "B1", 1, new Money(10m, TestValues.IRR));
        sale1.Submit(SaleNumber.Create(1405, 41));
        typeof(Sale).GetProperty(nameof(Sale.Total))!
            .SetValue(sale1, new Money(30m, TestValues.IRR));
        typeof(Sale).GetProperty(nameof(Sale.SubmittedAtUtc))!.SetValue(
            sale1, new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc));
        typeof(Sale).GetField("<CreatedAtUtc>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
            sale1, new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc));

        var sale2 = MakeSale(SaleStatus.Approved, 10m, line: (productA1.Id, "A1", 1, 10m));
        var draftSale = MakeSale(SaleStatus.Draft, 10m, line: (productB1.Id, "B1", 1, 10m));

        await repo.AddAsync(sale1, CancellationToken.None);
        await repo.AddAsync(sale2, CancellationToken.None);
        await repo.AddAsync(draftSale, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        await using (db)
        {
            // All-time (fromUtc = null) — the dashboard's default card.
            var rows = await repo.GetCategorySalesCountsAsync(
                null,
                new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new AllSalesSpecification(),
                CancellationToken.None);

            rows.Should().HaveCount(2);
            rows.Single(r => r.CategoryId == catA).SalesCount.Should().Be(2,
                "sale1 counts once despite two Cat-A line items; sale2 adds one more");
            rows.Single(r => r.CategoryId == catB).SalesCount.Should().Be(1,
                "sale1's Cat-B line item counts; the draft's does not");

            // Windowed: a window that excludes the sales entirely.
            var empty = await repo.GetCategorySalesCountsAsync(
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new AllSalesSpecification(),
                CancellationToken.None);
            empty.Should().BeEmpty("no revenue-eligible sale is anchored in the window");
        }
    }

    // ── GetTopPurchasersAsync ─────────────────────────────────────────

    [Fact]
    public async Task TopPurchasers_GroupsByCustomerAndOrdersByAmount()
    {
        var alice = TestValues.CustomerId;
        var bob = Guid.Parse("deadbeef-dead-beef-dead-beefdeadbeef");

        var sales = new[]
        {
            MakeSale(SaleStatus.Pending, 100m, customerId: alice, customerName: "Alice"),
            MakeSale(SaleStatus.Approved, 150m, customerId: alice, customerName: "Alice"),
            MakeSale(SaleStatus.Pending, 400m, customerId: bob, customerName: "Bob"),
            MakeSale(SaleStatus.Cancelled, 999m, customerId: alice, customerName: "Alice"),
            MakeSale(SaleStatus.Draft, 999m, customerId: bob, customerName: "Bob"),
        };
        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var rows = await repo.GetTopPurchasersAsync(
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                top: 4,
                new AllSalesSpecification(),
                CancellationToken.None);

            rows.Should().HaveCount(2);
            rows.First().Should().Match<TopPurchaserRow>(r =>
                r.CustomerId == bob && r.CustomerName == "Bob" && r.TotalAmountRaw == 400m);
            rows.Skip(1).Single().Should().Match<TopPurchaserRow>(r =>
                r.CustomerId == alice && r.TotalAmountRaw == 250m,
                "Alice's two eligible sales sum; her cancelled and draft sales don't count");
        }
    }

    // ── GetOldestPendingSaleAnchorAsync ───────────────────────────────

    [Fact]
    public async Task OldestPendingAnchor_ReturnsMinimumAnchorAmongPending()
    {
        var oldest = MakeSale(SaleStatus.Pending, 10m,
            new DateTime(2026, 6, 1, 5, 0, 0, DateTimeKind.Utc));
        var newest = MakeSale(SaleStatus.Pending, 20m,
            new DateTime(2026, 6, 10, 5, 0, 0, DateTimeKind.Utc));
        var approvedOld = MakeSale(SaleStatus.Approved, 30m,
            new DateTime(2026, 5, 1, 5, 0, 0, DateTimeKind.Utc));

        var (repo, db) = await CreateSeededAsync(oldest, newest, approvedOld);
        await using (db)
        {
            var anchor = await repo.GetOldestPendingSaleAnchorAsync(
                new AllSalesSpecification(), CancellationToken.None);

            anchor.Should().Be(new DateTime(2026, 6, 1, 5, 0, 0, DateTimeKind.Utc),
                "the MIN over pending anchors only — the older approved sale is ignored");
        }
    }

    [Fact]
    public async Task OldestPendingAnchor_NoPendingSales_ReturnsNull()
    {
        var approved = MakeSale(SaleStatus.Approved, 10m);
        var (repo, db) = await CreateSeededAsync(approved);
        await using (db)
        {
            var anchor = await repo.GetOldestPendingSaleAnchorAsync(
                new AllSalesSpecification(), CancellationToken.None);

            anchor.Should().BeNull();
        }
    }

    // ── CountDistinctPurchasersAsync ──────────────────────────────────

    [Fact]
    public async Task CountDistinctPurchasers_CountsUniqueCustomersInWindow()
    {
        var alice = TestValues.CustomerId;
        var bob = Guid.Parse("deadbeef-dead-beef-dead-beefdeadbeef");
        var carol = Guid.Parse("feedfeed-feed-feed-feed-feedfeedfeed");

        var sales = new[]
        {
            MakeSale(SaleStatus.Pending, 10m, customerId: alice),
            MakeSale(SaleStatus.Invoiced, 20m, customerId: alice), // same customer — counts once
            MakeSale(SaleStatus.Approved, 30m, customerId: bob,
                anchorUtc: new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc)), // outside window
            MakeSale(SaleStatus.Cancelled, 40m, customerId: carol), // cancelled — never counts
        };
        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var count = await repo.CountDistinctPurchasersAsync(
                new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                new AllSalesSpecification(),
                CancellationToken.None);

            count.Should().Be(1, "only Alice has an eligible sale inside the June window");
        }
    }
}

using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using TakOne.Application.Sales.Queries.GetSalesPaginated;
using TakOne.Application.Sales.Specifications;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the sales list's Round 4 server-side filters +
/// sort: every new WHERE/ORDER BY clause must translate to real SQL and
/// behave correctly against a live EF provider (SQLite here — the same
/// provider the rest of the integration suite uses; production runs SQL
/// Server, whose EF translator shares the owned/complex-property
/// flattening these clauses rely on).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY REAL-DB TESTS (vs. the in-memory unit tests)</b>: the unit
/// suite compiles the spec expressions against POCO lists, which proves
/// the LOGIC but not the TRANSLATION. A clause like
/// <c>sale.SaleNumber == null</c> (optional owned type),
/// <c>sale.Total.Amount &gt; v</c> (complex property), or
/// <c>sale.CustomerName.ToLower().Contains(term)</c> could be logically
/// perfect and still blow up as <c>InvalidOperationException: The LINQ
/// expression could not be translated</c> at runtime. These tests are
/// the contract that keeps the SQL translation honest.
/// </para>
/// <para>
/// <b>SEEDING</b>: sales are built via <see cref="Sale.Create"/> and
/// then configured (sale number, total, status, timestamp) through the
/// same test-only reflection the unit suite uses — the domain API
/// stays immutable in production code.
/// </para>
/// </remarks>
public class SalesListFilteringIntegrationTests
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
    /// Builds a sale with the given filter-relevant state. Only the
    /// members under test are configured; everything else keeps the
    /// <see cref="Sale.Create"/> defaults.
    /// </summary>
    private static Sale MakeSale(
        string customerName = "Alice Customer",
        decimal totalAmount = 0m,
        int? year = null,
        int? sequence = null,
        SaleStatus status = SaleStatus.Draft,
        DateTime? createdAtUtc = null,
        string createdByName = "Test Staff",
        Guid? customerId = null)
    {
        var sale = Sale.Create(
            customerId ?? TestValues.CustomerId,
            customerName,
            TestValues.CreatedByUserId,
            createdByName);

        if (year.HasValue && sequence.HasValue)
        {
            typeof(Sale).GetProperty(nameof(Sale.SaleNumber))!
                .SetValue(sale, SaleNumber.Create(year.Value, sequence.Value));
        }

        typeof(Sale).GetProperty(nameof(Sale.Total))!
            .SetValue(sale, new Money(totalAmount, TestValues.IRR));

        typeof(Sale).GetProperty(nameof(Sale.Status))!.SetValue(sale, status);

        if (createdAtUtc.HasValue)
        {
            typeof(Sale).GetField("<CreatedAtUtc>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(sale, createdAtUtc.Value);
        }

        return sale;
    }

    // ── SearchTerm (the legacy cross-column OR, now server-side) ──────

    [Fact]
    public async Task SearchTerm_FullNumber_FindsExactSaleAcrossAllPages()
    {
        // 25 sales on page size 10 — the target sits on page 3, which
        // the pre-Round-4 in-memory search could never reach (it only
        // filtered the loaded page).
        var sales = Enumerable.Range(1, 25).Select(i =>
            MakeSale(customerName: $"Customer {i:00}", totalAmount: i * 10m,
                year: 1405, sequence: i,
                createdAtUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i))).ToArray();

        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, "INT-1405-23", null),
                1, 10, CancellationToken.None);

            result.TotalCount.Should().Be(1, "the full number matches exactly one sale");
            result.Items.Should().ContainSingle(s => s.SaleNumber!.Sequence == 23);
        }
    }

    [Fact]
    public async Task SearchTerm_CustomerName_MatchesCaseInsensitively()
    {
        var alice = MakeSale(customerName: "Alice Customer");
        var bob = MakeSale(customerName: "Bob Builder");
        var (repo, db) = await CreateSeededAsync(alice, bob);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, "BOB", null),
                1, 20, CancellationToken.None);

            result.TotalCount.Should().Be(1);
            result.Items.Should().ContainSingle(s => s.CustomerName == "Bob Builder");
        }
    }

    [Fact]
    public async Task SearchTerm_DraftKeyword_FindsAllDrafts()
    {
        var draft1 = MakeSale();
        var draft2 = MakeSale();
        var numbered = MakeSale(year: 1405, sequence: 1, status: SaleStatus.Pending);
        var (repo, db) = await CreateSeededAsync(draft1, draft2, numbered);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, "draft", null),
                1, 20, CancellationToken.None);

            result.TotalCount.Should().Be(2, "both drafts match; the numbered sale does not");
            result.Items.Should().OnlyContain(s => s.SaleNumber == null);
        }
    }

    [Fact]
    public async Task SearchTerm_BareInteger_MatchesSequenceOrYear()
    {
        var seq42 = MakeSale(year: 1405, sequence: 42, customerName: "Seq Holder");
        var year1405 = MakeSale(year: 1405, sequence: 1, customerName: "Year Holder");
        var other = MakeSale(year: 1406, sequence: 7);
        var (repo, db) = await CreateSeededAsync(seq42, year1405, other);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, "42", null),
                1, 20, CancellationToken.None);

            result.Items.Should().Contain(s => s.CustomerName == "Seq Holder",
                "sequence 42 matches the bare integer");
            result.Items.Should().NotContain(s => s.CustomerName == "Year Holder");
            result.Items.Should().NotContain(s => s.CustomerName == "Alice Customer");

            // Bare "1405" matches the year arm (and any sequence 1405).
            var byYear = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, "1405", null),
                1, 20, CancellationToken.None);
            byYear.Items.Should().Contain(s => s.CustomerName == "Year Holder");
        }
    }

    [Fact]
    public async Task SearchTerm_Garbage_MatchesNothing()
    {
        var sale = MakeSale(year: 1405, sequence: 1);
        var (repo, db) = await CreateSeededAsync(sale);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, "zzz-xyz", null),
                1, 20, CancellationToken.None);

            result.TotalCount.Should().Be(0);
            result.Items.Should().BeEmpty();
        }
    }

    // ── SaleNumberTerm (the column filter) ────────────────────────────

    [Fact]
    public async Task SaleNumberTerm_YearOnly_MatchesEverySaleOfThatYear()
    {
        var y1405a = MakeSale(year: 1405, sequence: 1);
        var y1405b = MakeSale(year: 1405, sequence: 99);
        var y1406 = MakeSale(year: 1406, sequence: 2);
        var draft = MakeSale();
        var (repo, db) = await CreateSeededAsync(y1405a, y1405b, y1406, draft);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null,
                    new SalesListFilters("INT-1405", null, null, null, null, false)),
                1, 20, CancellationToken.None);

            result.TotalCount.Should().Be(2);
            result.Items.Should().OnlyContain(s => s.SaleNumber!.Year == 1405);
        }
    }

    [Fact]
    public async Task SaleNumberTerm_PersianDigits_NormalizeToLatin()
    {
        // A Persian-keyboard user types ۱۴۰۵ — the parser must normalize
        // to 1405 and match the year.
        var y1405 = MakeSale(year: 1405, sequence: 1);
        var y1406 = MakeSale(year: 1406, sequence: 2);
        var (repo, db) = await CreateSeededAsync(y1405, y1406);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null,
                    new SalesListFilters("INT-۱۴۰۵", null, null, null, null, false)),
                1, 20, CancellationToken.None);

            result.TotalCount.Should().Be(1,
                "Persian digits normalize to Latin before parsing");
            result.Items.Should().ContainSingle(s => s.SaleNumber!.Year == 1405);
        }
    }

    // ── Text column filters (customer / creator names) ────────────────

    [Theory]
    [InlineData(SalesTextOperator.Contains, "bob", new string[] { "Bob Builder" })]
    [InlineData(SalesTextOperator.NotContains, "bob", new string[] { "Alice Customer", "Carol Cooper" })]
    [InlineData(SalesTextOperator.Equals, "bob builder", new string[] { "Bob Builder" })]
    [InlineData(SalesTextOperator.NotEquals, "bob builder", new string[] { "Alice Customer", "Carol Cooper" })]
    [InlineData(SalesTextOperator.StartsWith, "bo", new string[] { "Bob Builder" })]
    [InlineData(SalesTextOperator.EndsWith, "cooper", new string[] { "Carol Cooper" })]
    public async Task CustomerNameFilter_AllOperators_TranslateAndFilter(
        SalesTextOperator op, string term, string[] expectedNames)
    {
        var sales = new[]
        {
            MakeSale(customerName: "Bob Builder"),
            MakeSale(customerName: "Alice Customer"),
            MakeSale(customerName: "Carol Cooper")
        };
        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null,
                    new SalesListFilters(null, new SalesTextFilter(term, op), null, null, null, false)),
                1, 20, CancellationToken.None);

            result.Items.Select(s => s.CustomerName)
                .Should().BeEquivalentTo(expectedNames);
            result.TotalCount.Should().Be(expectedNames.Length);
        }
    }

    [Fact]
    public async Task CreatedByNameFilter_Contains_Translates()
    {
        var byAlice = MakeSale(createdByName: "Alice Staff");
        var byBob = MakeSale(createdByName: "Bob Staff");
        var (repo, db) = await CreateSeededAsync(byAlice, byBob);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null,
                    new SalesListFilters(null, null,
                        new SalesTextFilter("alice", SalesTextOperator.Contains), null, null, false)),
                1, 20, CancellationToken.None);

            result.TotalCount.Should().Be(1);
            result.Items.Should().ContainSingle(s => s.CreatedByName == "Alice Staff");
        }
    }

    // ── Total amount filter (complex property comparison) ─────────────

    // NOTE: decimal is not a legal attribute-constant type in C#, so
    // the theory carries ints and the test converts to decimal.
    [Theory]
    [InlineData(SalesAmountOperator.GreaterThan, 100, new int[] { 150, 500 })]
    [InlineData(SalesAmountOperator.GreaterThanOrEqual, 100, new int[] { 100, 150, 500 })]
    [InlineData(SalesAmountOperator.LessThan, 100, new int[] { 50 })]
    [InlineData(SalesAmountOperator.LessThanOrEqual, 100, new int[] { 50, 100 })]
    [InlineData(SalesAmountOperator.Equals, 100, new int[] { 100 })]
    [InlineData(SalesAmountOperator.NotEquals, 100, new int[] { 50, 150, 500 })]
    public async Task TotalFilter_AllOperators_TranslateAndFilter(
        SalesAmountOperator op, int operand, int[] expectedAmounts)
    {
        var sales = new[]
        {
            MakeSale(totalAmount: 50m),
            MakeSale(totalAmount: 100m),
            MakeSale(totalAmount: 150m),
            MakeSale(totalAmount: 500m)
        };
        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null,
                    new SalesListFilters(null, null, null,
                        new SalesAmountFilter(op, operand), null, false)),
                1, 20, CancellationToken.None);

            result.Items.Select(s => s.Total.Amount)
                .Should().BeEquivalentTo(expectedAmounts.Select(a => (decimal)a));
            result.TotalCount.Should().Be(expectedAmounts.Length);
        }
    }

    // ── Status + date range (pre-Round 4 clauses still compose) ───────

    [Fact]
    public async Task StatusAndDateRange_ComposeWithColumnFilters()
    {
        var inRange = MakeSale(
            status: SaleStatus.Pending,
            totalAmount: 200m,
            createdAtUtc: new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));
        var wrongStatus = MakeSale(
            status: SaleStatus.Approved,
            totalAmount: 200m,
            createdAtUtc: new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));
        var outOfRange = MakeSale(
            status: SaleStatus.Pending,
            totalAmount: 200m,
            createdAtUtc: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var wrongAmount = MakeSale(
            status: SaleStatus.Pending,
            totalAmount: 10m,
            createdAtUtc: new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));

        var (repo, db) = await CreateSeededAsync(inRange, wrongStatus, outOfRange, wrongAmount);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(
                    SaleStatus.Pending,
                    new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                    null,
                    new SalesListFilters(null, null, null,
                        new SalesAmountFilter(SalesAmountOperator.GreaterThan, 100m), null, false)),
                1, 20, CancellationToken.None);

            result.TotalCount.Should().Be(1);
            result.Items.Should().ContainSingle(s => s.Id == inRange.Id);
        }
    }

    // ── Sorting ───────────────────────────────────────────────────────

    [Fact]
    public async Task Sort_ByTotalAscending_OrdersPagesDeterministically()
    {
        // 15 sales with shuffled totals, page size 5: page 2 must start
        // exactly where page 1 ended (the Id tiebreaker guarantees no
        // skips/dupes across pages).
        var totals = new[] { 300m, 10m, 250m, 20m, 200m, 30m, 150m, 40m, 100m, 50m, 90m, 60m, 80m, 70m, 70m };
        var sales = totals.Select(t => MakeSale(totalAmount: t)).ToArray();
        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var page1 = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null,
                    new SalesListFilters(null, null, null, null, SalesSortBy.Total, false)),
                1, 5, CancellationToken.None);
            var page2 = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null,
                    new SalesListFilters(null, null, null, null, SalesSortBy.Total, false)),
                2, 5, CancellationToken.None);

            page1.TotalCount.Should().Be(15);
            page1.Items.Select(s => s.Total.Amount).Should().BeInAscendingOrder();

            // The boundary: page 1 ends with the highest of the first 5;
            // page 2 starts at or after it (equal totals ordered by Id).
            var lastOfPage1 = page1.Items[^1].Total.Amount;
            page2.Items[0].Total.Amount.Should().BeGreaterThanOrEqualTo(lastOfPage1);

            // No sale appears on both pages (the Id tiebreaker's job).
            page1.Items.Select(s => s.Id)
                .Should().NotIntersectWith(page2.Items.Select(s => s.Id));
        }
    }

    [Fact]
    public async Task Sort_BySaleNumberAscending_DraftsSortAsLeadingBlock()
    {
        var draft1 = MakeSale();
        var draft2 = MakeSale();
        var y1405s1 = MakeSale(year: 1405, sequence: 1);
        var y1405s2 = MakeSale(year: 1405, sequence: 2);
        var y1406s1 = MakeSale(year: 1406, sequence: 1);

        var (repo, db) = await CreateSeededAsync(draft2, y1406s1, y1405s2, draft1, y1405s1);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null,
                    new SalesListFilters(null, null, null, null, SalesSortBy.SaleNumber, false)),
                1, 20, CancellationToken.None);

            result.Items.Select(s => s.SaleNumber is null ? "DRAFT" : s.SaleNumber.Value)
                .Should().ContainInOrder(
                    (IEnumerable<string>)new[]
                    {
                        "DRAFT", "DRAFT",
                        y1405s1.SaleNumber!.Value,
                        y1405s2.SaleNumber!.Value,
                        y1406s1.SaleNumber!.Value
                    },
                    "NULL owned navigations sort as a leading block on ASC, then Year/Sequence order");
        }
    }

    [Fact]
    public async Task Sort_Default_IsNewestFirst()
    {
        var oldest = MakeSale(createdAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var middle = MakeSale(createdAtUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = MakeSale(createdAtUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        var (repo, db) = await CreateSeededAsync(oldest, middle, newest);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null, null),
                1, 20, CancellationToken.None);

            result.Items.Select(s => s.Id)
                .Should().ContainInOrder(newest.Id, middle.Id, oldest.Id);
        }
    }

    // ── Customer scoping composes with everything ─────────────────────

    [Fact]
    public async Task CustomerScopedSpec_WithFiltersAndSort_StaysScoped()
    {
        var customerId = TestValues.CustomerId;
        var otherCustomerId = Guid.NewGuid();

        var own = MakeSale(customerId: customerId, customerName: "Own Sale",
            totalAmount: 500m, year: 1405, sequence: 1,
            createdAtUtc: new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc));
        var ownOther = MakeSale(customerId: customerId, customerName: "Own Other",
            totalAmount: 50m,
            createdAtUtc: new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc));
        var foreign = MakeSale(customerId: otherCustomerId, customerName: "Own Sale",
            totalAmount: 500m, year: 1405, sequence: 2,
            createdAtUtc: new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc));

        var (repo, db) = await CreateSeededAsync(own, ownOther, foreign);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new SaleByCustomerSpecification(
                    customerId,
                    status: null,
                    fromUtc: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
                    toUtcExclusive: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                    searchTerm: null,
                    filters: new SalesListFilters(
                        null, new SalesTextFilter("own", SalesTextOperator.Contains), null,
                        new SalesAmountFilter(SalesAmountOperator.GreaterThan, 100m),
                        SalesSortBy.Total, false)),
                1, 20, CancellationToken.None);

            result.TotalCount.Should().Be(1,
                "customer scope + name filter + amount filter intersect");
            result.Items.Should().ContainSingle(s => s.Id == own.Id);
        }
    }

    // ── Paging math (the core Round 4 promise) ────────────────────────

    [Fact]
    public async Task FiltersAndPaging_TotalCountStaysAccurate()
    {
        // 12 Bob sales + 8 Alice sales; filter to Bob, page size 5 →
        // TotalCount must say 12 (the pre-Round-4 search would have
        // reported the pre-filter count of the loaded page).
        var sales = Enumerable.Range(1, 12).Select(i =>
                MakeSale(customerName: $"Bob {i:00}", totalAmount: i * 10m))
            .Concat(Enumerable.Range(1, 8).Select(i => MakeSale(customerName: $"Alice {i:00}")))
            .ToArray();

        var (repo, db) = await CreateSeededAsync(sales);
        await using (db)
        {
            var result = await repo.GetPaginatedBySpecificationAsync(
                new AllSalesSpecification(null, null, null, null,
                    new SalesListFilters(null, new SalesTextFilter("bob", SalesTextOperator.Contains),
                        null, null, null, true)),
                2, 5, CancellationToken.None);

            result.TotalCount.Should().Be(12,
                "the filter rides in SQL, so the count is the FILTERED count");
            result.Items.Should().HaveCount(5, "page 2 of 12 rows @ 5/page");
            result.Items.Should().OnlyContain(s => s.CustomerName.StartsWith("Bob"));
        }
    }
}

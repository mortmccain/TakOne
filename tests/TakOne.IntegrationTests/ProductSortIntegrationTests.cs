using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.Queries.GetProductsPaginated;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.ValueObjects;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the product catalog's Round 4 sort orders —
/// the repository's parameterized ORDER BY must translate to real SQL
/// and page deterministically on a live EF provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY REAL-DB TESTS</b>: ordering by <c>p.Price.Amount</c> (a Money
/// complex property) and paging on top of it depends on EF's SQL
/// translation — the in-memory mocks can't prove translatability. The
/// SQLite provider here shares the complex-property flattening the
/// production SQL Server provider uses.
/// </para>
/// <para>
/// <b>DETERMINISM CONTRACT</b>: every non-default order appends the
/// product NAME as a tiebreaker, so OFFSET/FETCH never skips or
/// duplicates rows when products share a price (the tests seed equal
/// prices deliberately to pin this).
/// </para>
/// </remarks>
public class ProductSortIntegrationTests
{
    /// <summary>
    /// Creates the SQLite db, seeds one category, then creates the named
    /// products (with prices) under it. Product.Create validates the
    /// category id up front, so the category must exist first.
    /// </summary>
    private static async Task<(ProductRepository repo, ApplicationDbContext db)> CreateSeededAsync(
        params (string Name, decimal Price)[] products)
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var repo = new ProductRepository(db);

        db.Categories.Add(Category.Create("Sort Test Category"));
        await db.SaveChangesAsync();
        var categoryId = db.Categories.First().Id;

        foreach (var (name, price) in products)
        {
            await repo.AddAsync(Product.Create(
                name,
                description: $"Test product {name}",
                price: new Money(price, "IRR"),
                stockQuantity: 100,
                categoryId: categoryId), CancellationToken.None);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        return (repo, db);
    }

    [Fact]
    public async Task GetPaginatedAsync_DefaultOrder_IsNameAscending()
    {
        // Arrange — insertion order deliberately differs from name order.
        var (repo, db) = await CreateSeededAsync(
            ("Cherry", 30m), ("Apple", 10m), ("Banana", 20m));
        await using (db)
        {
            // Act
            var result = await repo.GetPaginatedAsync(pageNumber: 1, pageSize: 10);

            // Assert — the pre-Round-4 default is unchanged.
            result.Items.Select(p => p.Name)
                .Should().ContainInOrder("Apple", "Banana", "Cherry");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_PriceLowToHigh_OrdersByAmount()
    {
        var (repo, db) = await CreateSeededAsync(
            ("Expensive", 500m), ("Cheap", 5m), ("Middle", 50m));
        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                pageNumber: 1, pageSize: 10, sortBy: ProductSortBy.PriceLowToHigh);

            result.Items.Select(p => p.Name)
                .Should().ContainInOrder("Cheap", "Middle", "Expensive");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_PriceHighToLow_OrdersByAmountDescending()
    {
        var (repo, db) = await CreateSeededAsync(
            ("Expensive", 500m), ("Cheap", 5m), ("Middle", 50m));
        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                pageNumber: 1, pageSize: 10, sortBy: ProductSortBy.PriceHighToLow);

            result.Items.Select(p => p.Name)
                .Should().ContainInOrder("Expensive", "Middle", "Cheap");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_EqualPrices_TiebreakByNameAcrossPages()
    {
        // Arrange — SIX products at the SAME price; names deliberately
        // NOT in insertion order. Page size 2 → three pages; every page
        // boundary must respect the name tiebreaker (no skips/dupes).
        var (repo, db) = await CreateSeededAsync(
            ("Delta", 42m), ("Alpha", 42m), ("Echo", 42m),
            ("Bravo", 42m), ("Foxtrot", 42m), ("Charlie", 42m));
        await using (db)
        {
            // Act + Assert — concatenate all three pages under the price
            // order; the result must be exactly the six names, each once,
            // in name order (the tiebreaker).
            var all = new List<string>();
            for (var page = 1; page <= 3; page++)
            {
                var result = await repo.GetPaginatedAsync(
                    pageNumber: page, pageSize: 2, sortBy: ProductSortBy.PriceLowToHigh);
                result.TotalCount.Should().Be(6);
                all.AddRange(result.Items.Select(p => p.Name));
            }

            all.Should().BeEquivalentTo(new[]
            {
                "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot"
            });
            all.Should().BeInAscendingOrder(
                "the name tiebreaker keeps equal-priced products deterministic across pages");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_SortComposesWithSearchFilter()
    {
        // The sort must apply INSIDE the filtered query (a search for
        // "berry" that returns Strawberry + Blueberry + Razzleberry
        // should come back price-ordered, not name-ordered).
        var (repo, db) = await CreateSeededAsync(
            ("Strawberry", 30m), ("Blueberry", 10m),
            ("Razzleberry", 20m), ("Apple", 99m));
        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                searchTerm: "berry",
                pageNumber: 1, pageSize: 10,
                sortBy: ProductSortBy.PriceLowToHigh);

            result.TotalCount.Should().Be(3);
            result.Items.Select(p => p.Name)
                .Should().ContainInOrder("Blueberry", "Razzleberry", "Strawberry");
        }
    }
}

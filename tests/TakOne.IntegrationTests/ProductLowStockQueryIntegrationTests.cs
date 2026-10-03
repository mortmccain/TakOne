using FluentAssertions;
using TakOne.Application.Common.Models;
using TakOne.Domain.Products.Entities;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="ProductRepository.GetLowStockProductsAsync"/>
/// — the Dashboard restock alert's query. Mirrors the other repository
/// integration suites: the WHERE (IsActive + stock ceiling), the ORDER BY
/// (stock ascending, then name — deterministic order at equal stock), and
/// the TOP cap must all translate to real SQL and behave correctly
/// (production runs SQL Server; this suite pins the LINQ→SQL contract on
/// SQLite, which flags the same translation failures).
/// </summary>
public class ProductLowStockQueryIntegrationTests
{
    private static async Task<ProductRepository> CreateSeededAsync(params Product[] products)
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var repo = new ProductRepository(db);

        foreach (var product in products)
        {
            await repo.AddAsync(product, CancellationToken.None);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        return repo;
    }

    [Fact]
    public async Task GetLowStock_ReturnsActiveProductsAtOrBelowThreshold_StockAscending()
    {
        // Arrange — a spread of stock levels around the threshold (10):
        // 0 (out), 4, 10 (boundary — INCLUSIVE), 11 (just above — out),
        // 55 (healthy — out).
        var outOfStock = new ProductBuilder().WithName("C-OutOfStock").WithStock(0).Build();
        var lowA = new ProductBuilder().WithName("A-Low").WithStock(4).Build();
        var boundary = new ProductBuilder().WithName("B-Boundary").WithStock(10).Build();
        var justAbove = new ProductBuilder().WithName("D-JustAbove").WithStock(11).Build();
        var healthy = new ProductBuilder().WithName("E-Healthy").WithStock(55).Build();

        var repo = await CreateSeededAsync(healthy, justAbove, boundary, lowA, outOfStock);

        // Act
        var result = await repo.GetLowStockProductsAsync(
            ProductStockThresholds.LowStock, take: 6, CancellationToken.None);

        // Assert — only the ≤10 set, most critical first (0 leads).
        var names = result.Select(p => p.Name).ToList();
        names.Should().Equal("C-OutOfStock", "A-Low", "B-Boundary");
    }

    [Fact]
    public async Task GetLowStock_ExcludesDeactivatedProducts()
    {
        // Arrange — a deactivated product has its stock zeroed by the
        // domain (Deactivate), so it would match on stock alone; the
        // IsActive filter must keep it OUT of the alert (a deactivated
        // product can't be bought — "restock it" would be noise).
        var active = new ProductBuilder().WithName("Active-Low").WithStock(3).Build();
        var deactivated = new ProductBuilder().WithName("Deactivated").WithStock(20).Build();
        deactivated.Deactivate(); // zeroes stock + flips IsActive

        var repo = await CreateSeededAsync(active, deactivated);

        // Act
        var result = await repo.GetLowStockProductsAsync(
            ProductStockThresholds.LowStock, take: 6, CancellationToken.None);

        // Assert
        result.Should().ContainSingle()
              .Which.Name.Should().Be("Active-Low");
    }

    [Fact]
    public async Task GetLowStock_AtEqualStock_OrdersByNameAscending()
    {
        // Arrange — three products at the same (low) stock: the name
        // tiebreaker keeps the alert from reshuffling between loads.
        var zebra = new ProductBuilder().WithName("Zebra").WithStock(5).Build();
        var alpha = new ProductBuilder().WithName("Alpha").WithStock(5).Build();
        var mike = new ProductBuilder().WithName("Mike").WithStock(5).Build();

        var repo = await CreateSeededAsync(zebra, mike, alpha);

        // Act
        var result = await repo.GetLowStockProductsAsync(
            ProductStockThresholds.LowStock, take: 6, CancellationToken.None);

        // Assert
        result.Select(p => p.Name).Should().Equal("Alpha", "Mike", "Zebra");
    }

    [Fact]
    public async Task GetLowStock_CapsAtTake_MostCriticalFirst()
    {
        // Arrange — 8 low-stock products, take=6: the 6 SMALLEST stocks
        // make the cut (the SQL TOP is applied AFTER the ORDER BY).
        var products = Enumerable.Range(1, 8)
            .Select(i => new ProductBuilder()
                .WithName($"P{i:00}")
                .WithStock(i))          // 1..8, all ≤ 10
            .Select(b => b.Build())
            .ToArray();

        var repo = await CreateSeededAsync(products);

        // Act
        var result = await repo.GetLowStockProductsAsync(
            ProductStockThresholds.LowStock, take: 6, CancellationToken.None);

        // Assert — P01..P06 (stocks 1..6); P07/P08 (7, 8) fall off.
        result.Should().HaveCount(6);
        result.Select(p => p.Name).Should().Equal("P01", "P02", "P03", "P04", "P05", "P06");
    }

    [Fact]
    public async Task GetLowStock_NoMatches_ReturnsEmptyList()
    {
        // Arrange — everything healthy.
        var healthy1 = new ProductBuilder().WithName("H1").WithStock(42).Build();
        var healthy2 = new ProductBuilder().WithName("H2").WithStock(500).Build();

        var repo = await CreateSeededAsync(healthy1, healthy2);

        // Act
        var result = await repo.GetLowStockProductsAsync(
            ProductStockThresholds.LowStock, take: 6, CancellationToken.None);

        // Assert — empty, not null (the alert renders "all good" = hidden).
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task CountLowStock_MatchesTheListWhereClause_Uncapped()
    {
        // Arrange — 8 low-stock ACTIVE products (more than the list's
        // take cap), 2 healthy, 1 deactivated.
        var products = Enumerable.Range(1, 8)
            .Select(i => new ProductBuilder().WithName($"L{i:00}").WithStock(i))
            .Select(b => b.Build())
            .ToList();
        products.Add(new ProductBuilder().WithName("H1").WithStock(42).Build());
        products.Add(new ProductBuilder().WithName("H2").WithStock(500).Build());
        var deactivated = new ProductBuilder().WithName("D1").WithStock(20).Build();
        deactivated.Deactivate(); // zeroes stock + flips IsActive
        products.Add(deactivated);

        var repo = await CreateSeededAsync(products.ToArray());

        // Act — count with the SAME threshold the dashboard uses.
        var count = await repo.CountLowStockAsync(
            ProductStockThresholds.LowStock, CancellationToken.None);

        // Assert — 8 (every low ACTIVE product, NOT capped by take; the
        // deactivated one's zeroed stock must not sneak it in).
        count.Should().Be(8);

        // The list with take=6 is a CAPPED subset of exactly that count.
        var list = await repo.GetLowStockProductsAsync(
            ProductStockThresholds.LowStock, take: 6, CancellationToken.None);
        list.Should().HaveCount(6);
    }

    [Fact]
    public async Task CountLowStock_NoMatches_ReturnsZero()
    {
        // Arrange — everything healthy.
        var repo = await CreateSeededAsync(
            new ProductBuilder().WithName("H1").WithStock(42).Build());

        // Act
        var count = await repo.CountLowStockAsync(
            ProductStockThresholds.LowStock, CancellationToken.None);

        // Assert
        count.Should().Be(0);
    }
}

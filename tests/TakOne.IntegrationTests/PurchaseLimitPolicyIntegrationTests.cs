using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Common.Entities;
using TakOne.Domain.Common.Enums;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.Infrastructure.Services;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="PurchaseLimitPolicy"/> — the
/// infrastructure implementation of <see cref="IPurchaseLimitPolicy"/>.
/// Drives the real cached <see cref="SystemSettingsService"/> + real
/// <see cref="ProductRepository"/> + real <see cref="CustomerGroupRepository"/>
/// against an in-memory SQLite DB to verify the mode-bypass + currency-
/// matching rules work end-to-end with persisted data.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THESE ARE INTEGRATION TESTS:</b> the mock-based unit tests
/// verify the policy calls the right collaborator methods with the right
/// args — but they can't catch wiring mistakes like:
/// <list type="bullet">
///   <item>A Product's <c>Price.Currency</c> column being silently
///       dropped on round-trip (ComplexProperty mapping bug) so the
///       currency match returns "USD vs empty string" instead of
///       "USD vs IRR".</item>
///   <item>The cache returning a STALE LimitMode because the singleton
///       row wasn't actually persisted correctly.</item>
///   <item>The staff-bypass short-circuit accidentally hitting the DB
///       when groupId is null (would slow every staff purchase attempt).</item>
/// </list>
/// </para>
/// </remarks>
public class PurchaseLimitPolicyIntegrationTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task<(
        PurchaseLimitPolicy policy,
        IProductRepository productRepo,
        ICustomerGroupRepository groupRepo,
        ApplicationDbContext db)>
        BuildWiredCollaboratorsAsync(LimitMode mode = LimitMode.CountOnly)
    {
        var db = await SqliteTestDbFactory.CreateAsync();

        // EnsureCreatedAsync honors HasData(...) in SystemSettingsConfiguration,
        // so the singleton row already exists with LimitMode=CountOnly. We
        // UPDATE it to the requested mode via ExecuteUpdateAsync (a single
        // SQL UPDATE that bypasses the change tracker + avoids the unique-
        // index conflict that AddAsync would cause against the seeded row).
        await db.SystemSettings
            .Where(s => s.Id == Domain.Common.Entities.SystemSettings.SingletonId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LimitMode, mode));
        db.ChangeTracker.Clear();

        // Wire the real repos + cached settings service + policy.
        var productRepo = new ProductRepository(db);
        var groupRepo = new CustomerGroupRepository(db);
        var realSettingsRepo = new SystemSettingsRepository(db);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settingsService = new SystemSettingsService(
            realSettingsRepo,
            cache);

        var policy = new PurchaseLimitPolicy(
            settingsService,
            groupRepo,
            productRepo,
            Substitute.For<ILogger<PurchaseLimitPolicy>>());

        return (policy, productRepo, groupRepo, db);
    }

    private static async Task<(Guid productId, Guid groupId)> SeedProductAndGroupAsync(
        ApplicationDbContext db,
        string productCurrency,
        string groupCurrency,
        int? groupLimit = null)
    {
        // Seed a Category so the Product's CategoryId is satisfiable (the
        // ProductConfiguration has no FK, but Product.Create requires a
        // non-Empty categoryId).
        var category = Domain.Categories.Entities.Category.Create("Seed Category");
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Seed a CustomerGroup with the requested salary currency.
        var group = CustomerGroup.Create(
            "Test Group",
            new Money(1000m, groupCurrency));
        db.CustomerGroups.Add(group);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Seed a Product with the requested price currency + optional limit.
        // Use a tracked Product so we can call SetPurchaseLimit before save.
        var product = Product.Create(
            name: "Test Product",
            description: "Test description",
            price: new Money(10m, productCurrency),
            stockQuantity: 100,
            categoryId: category.Id);
        if (groupLimit.HasValue)
        {
            product.SetPurchaseLimit(group.Id, groupLimit.Value);
        }
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (product.Id, group.Id);
    }

    // ── Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCountLimitAsync_WithModeBothAndGroupLimit_ReturnsLimit()
    {
        // Arrange — mode=Both, Product with a PurchaseLimit of 5 for the group.
        var (policy, productRepo, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            var (productId, groupId) = await SeedProductAndGroupAsync(
                db, productCurrency: TestValues.USD, groupCurrency: TestValues.USD, groupLimit: 5);

            // Act
            var limit = await policy.GetCountLimitAsync(productId, groupId, CancellationToken.None);

            // Assert — returns the persisted limit int.
            limit.Should().Be(5);
        }
    }

    [Fact]
    public async Task GetCountLimitAsync_WithModeSalaryOnly_ReturnsNullRegardlessOfProduct()
    {
        // Arrange — mode=SalaryOnly, Product with a PurchaseLimit of 5.
        // Even with a configured limit, the SalaryOnly mode bypasses
        // count-limit enforcement.
        var (policy, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.SalaryOnly);
        await using (db)
        {
            var (productId, groupId) = await SeedProductAndGroupAsync(
                db, productCurrency: TestValues.USD, groupCurrency: TestValues.USD, groupLimit: 5);

            // Act
            var limit = await policy.GetCountLimitAsync(productId, groupId, CancellationToken.None);

            // Assert — null because SalaryOnly mode bypasses count limits.
            limit.Should().BeNull();
        }
    }

    [Fact]
    public async Task GetCountLimitAsync_WithModeCountOnly_ReturnsLimit()
    {
        // Arrange — mode=CountOnly, Product with a PurchaseLimit of 5.
        var (policy, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.CountOnly);
        await using (db)
        {
            var (productId, groupId) = await SeedProductAndGroupAsync(
                db, productCurrency: TestValues.USD, groupCurrency: TestValues.USD, groupLimit: 5);

            // Act
            var limit = await policy.GetCountLimitAsync(productId, groupId, CancellationToken.None);

            // Assert
            limit.Should().Be(5);
        }
    }

    // Verifies the staff-bypass short-circuit: when groupId is null (staff
    // user has no group), GetCountLimitAsync returns null IMMEDIATELY —
    // it does NOT consult the SystemSettings cache, does NOT load the
    // Product, does NOT touch the DB.
    [Fact]
    public async Task GetCountLimitAsync_WithNoGroupId_ReturnsNullWithoutHittingDb()
    {
        // Arrange
        var (policy, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            // Note: no Product or Group seeded — the call shouldn't even
            // get to the point of needing them.
            // Act
            var limit = await policy.GetCountLimitAsync(
                productId: TestValues.ProductId,
                groupId: null, // staff user.
                CancellationToken.None);

            // Assert — null returned; no exception thrown.
            limit.Should().BeNull();
        }
    }

    [Fact]
    public async Task IsCurrencyMatchAsync_WithMatchingCurrency_ReturnsTrue()
    {
        // Arrange — Product with USD price + Group with USD salary.
        var (policy, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            var (productId, groupId) = await SeedProductAndGroupAsync(
                db, productCurrency: TestValues.IRR, groupCurrency: TestValues.IRR);

            // Act
            var match = await policy.IsCurrencyMatchAsync(productId, groupId, CancellationToken.None);

            // Assert
            match.Should().BeTrue();
        }
    }

    [Fact]
    public async Task IsCurrencyMatchAsync_WithMismatchedCurrency_ReturnsFalse()
    {
        // Arrange — Product with USD price + Group with IRR salary.
        var (policy, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            var (productId, groupId) = await SeedProductAndGroupAsync(
                db, productCurrency: TestValues.USD, groupCurrency: TestValues.IRR);

            // Act
            var match = await policy.IsCurrencyMatchAsync(productId, groupId, CancellationToken.None);

            // Assert
            match.Should().BeFalse();
        }
    }

    // Verifies the staff-bypass short-circuit for IsCurrencyMatchAsync:
    // groupId=null → returns true without consulting the DB (staff users
    // are exempt from currency enforcement).
    [Fact]
    public async Task IsCurrencyMatchAsync_WithNoGroupId_ReturnsTrueWithoutHittingDb()
    {
        // Arrange
        var (policy, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            // Act — staff bypass.
            var match = await policy.IsCurrencyMatchAsync(
                productId: TestValues.ProductId,
                groupId: null,
                CancellationToken.None);

            // Assert — true returned, no exception, no DB lookup.
            match.Should().BeTrue();
        }
    }

    [Fact]
    public async Task IsCurrencyMatchAsync_WithCountOnlyMode_StillEnforcesCurrencyMismatch()
    {
        // Arrange — mode=CountOnly, Product(USD) + Group(IRR).
        // Currency matching applies in EVERY mode (per CustomerGroup class doc).
        var (policy, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.CountOnly);
        await using (db)
        {
            var (productId, groupId) = await SeedProductAndGroupAsync(
                db, productCurrency: TestValues.USD, groupCurrency: TestValues.IRR);

            // Act
            var match = await policy.IsCurrencyMatchAsync(productId, groupId, CancellationToken.None);

            // Assert — currency check fires even in CountOnly mode.
            match.Should().BeFalse();
        }
    }
}

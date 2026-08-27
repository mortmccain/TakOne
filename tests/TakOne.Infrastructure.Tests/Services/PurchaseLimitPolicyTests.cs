using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Common.Enums;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.Infrastructure.Services;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PurchaseLimitPolicy"/>.
///
/// COVERAGE APPROACH:
///   The policy resolves the system LimitMode (via cached
///   <see cref="ISystemSettingsService"/>) and applies:
///     • IsCountLimitEnforcedAsync — true when mode is CountOnly or Both
///     • IsSalaryBudgetEnforcedAsync — true when mode is SalaryOnly or Both
///     • GetCountLimitAsync — null when mode is SalaryOnly OR groupId is null
///       OR product not found OR product has no limit for the group;
///       otherwise the limit int from the Product aggregate's lookup.
///     • IsCurrencyMatchAsync — true if groupId is null (staff bypass),
///       true if group/product is missing (defensive — handler will fail
///       later), otherwise string.Equals(product.Price.Currency,
///       group.Salary.Currency, Ordinal).
///
///   Currency matching ALWAYS applies — even when mode is CountOnly.
/// </summary>
public class PurchaseLimitPolicyTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    private const string IRR = "IRR";
    private const string USD = "USD";

    // Builds a fully-wired mock environment. The default mode is
    // CountOnly (which enforces count limits but not salary).
    private static (
        ISystemSettingsService systemSettings,
        ICustomerGroupRepository groupRepo,
        IProductRepository productRepo,
        ILogger<PurchaseLimitPolicy> logger)
        BuildMocks(LimitMode mode = LimitMode.CountOnly)
    {
        var systemSettings = Substitute.For<ISystemSettingsService>();
        systemSettings.GetLimitModeAsync(Arg.Any<CancellationToken>())
            .Returns(mode);
        var groupRepo = Substitute.For<ICustomerGroupRepository>();
        var productRepo = Substitute.For<IProductRepository>();
        var logger = Substitute.For<ILogger<PurchaseLimitPolicy>>();
        return (systemSettings, groupRepo, productRepo, logger);
    }

    // Builds a CustomerGroup with the supplied salary currency.
    private static CustomerGroup BuildGroup(Money salary)
        => CustomerGroup.Create("Test Group", salary);

    // Builds a Product with the supplied price. The ProductBuilder gives
    // sensible defaults; we override the price.
    private static Product BuildProduct(Money price)
        => new Testing.Builders.ProductBuilder()
            .WithName("Widget")
            .WithPrice(price)
            .Build();

    // ── IsCountLimitEnforcedAsync ────────────────────────────────────

    [Fact]
    public async Task IsCountLimitEnforcedAsync_WithCountOnly_ReturnsTrue()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsCountLimitEnforcedAsync(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsCountLimitEnforcedAsync_WithSalaryOnly_ReturnsFalse()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.SalaryOnly);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsCountLimitEnforcedAsync(CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsCountLimitEnforcedAsync_WithBoth_ReturnsTrue()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.Both);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsCountLimitEnforcedAsync(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    // ── IsSalaryBudgetEnforcedAsync ──────────────────────────────────

    [Fact]
    public async Task IsSalaryBudgetEnforcedAsync_WithSalaryOnly_ReturnsTrue()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.SalaryOnly);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsSalaryBudgetEnforcedAsync(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsSalaryBudgetEnforcedAsync_WithCountOnly_ReturnsFalse()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsSalaryBudgetEnforcedAsync(CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsSalaryBudgetEnforcedAsync_WithBoth_ReturnsTrue()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.Both);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsSalaryBudgetEnforcedAsync(CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    // ── GetCountLimitAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetCountLimitAsync_WithNullGroupId_ReturnsNullWithoutDbCall()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.GetCountLimitAsync(TestValues.ProductId, null, CancellationToken.None);

        // Assert
        // Staff (no group) bypass — short-circuit before reading mode or product.
        result.Should().BeNull();
        await systemSettings.DidNotReceive().GetLimitModeAsync(Arg.Any<CancellationToken>());
        await productRepo.DidNotReceive().GetByIdReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCountLimitAsync_WithSalaryOnlyMode_ReturnsNullWithoutProductLookup()
    {
        // Arrange
        // Mode is SalaryOnly — count limits are OFF. The service returns null
        // without touching the product repo.
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.SalaryOnly);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.GetCountLimitAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        await productRepo.DidNotReceive().GetByIdReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCountLimitAsync_WithCountOnlyAndLimit_ReturnsLimit()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var product = BuildProduct(new Money(100m, IRR));
        product.SetPurchaseLimit(TestValues.GroupId, 5);
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(product);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.GetCountLimitAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().Be(5);
    }

    [Fact]
    public async Task GetCountLimitAsync_WithBothModeAndLimit_ReturnsLimit()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.Both);
        var product = BuildProduct(new Money(100m, IRR));
        product.SetPurchaseLimit(TestValues.GroupId, 3);
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(product);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.GetCountLimitAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().Be(3);
    }

    [Fact]
    public async Task GetCountLimitAsync_WhenProductNotFound_ReturnsNullAndLogsWarning()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Product?)null);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.GetCountLimitAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        // Defensive warning — the handler should have validated product
        // existence first; if we're here, that check was bypassed.
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task GetCountLimitAsync_WhenProductHasNoLimitForGroup_ReturnsNull()
    {
        // Arrange
        // Product exists but has NO limit set for the supplied group.
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var product = BuildProduct(new Money(100m, IRR));
        // Note: NO SetPurchaseLimit call — the product has an empty
        // PurchaseLimits collection.
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(product);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.GetCountLimitAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        // No warning — a missing limit for a specific group is a valid
        // state (the product just doesn't restrict that group).
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── IsCurrencyMatchAsync ────────────────────────────────────────

    [Fact]
    public async Task IsCurrencyMatchAsync_WithNullGroupId_ReturnsTrueWithoutDbCall()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsCurrencyMatchAsync(TestValues.ProductId, null, CancellationToken.None);

        // Assert
        // Staff (no group) bypasses currency matching.
        result.Should().BeTrue();
        await groupRepo.DidNotReceive().GetByIdReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await productRepo.DidNotReceive().GetByIdReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsCurrencyMatchAsync_WhenGroupNotFound_ReturnsTrueWithWarning()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((CustomerGroup?)null);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsCurrencyMatchAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        // Group missing → return true (no constraint). The handler will
        // fail later at AddLineItem with a clearer error.
        result.Should().BeTrue();
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task IsCurrencyMatchAsync_WhenProductNotFound_ReturnsTrueWithWarning()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var group = BuildGroup(new Money(5000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Product?)null);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsCurrencyMatchAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task IsCurrencyMatchAsync_WhenCurrenciesMatch_ReturnsTrue()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var group = BuildGroup(new Money(5000m, IRR));
        var product = BuildProduct(new Money(100m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(product);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsCurrencyMatchAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsCurrencyMatchAsync_WhenCurrenciesMismatch_ReturnsFalse()
    {
        // Arrange
        // Group salary is in IRR; product is priced in USD — mismatch.
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var group = BuildGroup(new Money(5000m, IRR));
        var product = BuildProduct(new Money(100m, USD));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(product);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsCurrencyMatchAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    // ── Currency matching applies in EVERY mode (even CountOnly) ────

    // Even when mode is CountOnly, currency mismatch must be rejected.
    // This test guards against a future refactor that ties currency
    // matching to the salary-budget enforcement flag.
    [Fact]
    public async Task IsCurrencyMatchAsync_InCountOnlyMode_StillEnforcesCurrencyMismatch()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var group = BuildGroup(new Money(5000m, IRR));
        var product = BuildProduct(new Money(100m, USD));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(product);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);

        // Act
        var result = await sut.IsCurrencyMatchAsync(
            TestValues.ProductId, TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    // ── CancellationToken forwarding ─────────────────────────────────

    [Fact]
    public async Task IsCountLimitEnforcedAsync_ForwardsCancellationTokenToSystemSettings()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await sut.IsCountLimitEnforcedAsync(ct);

        // Assert
        await systemSettings.Received(1).GetLimitModeAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task GetCountLimitAsync_ForwardsCancellationTokenToProductRepo()
    {
        // Arrange
        var (systemSettings, groupRepo, productRepo, logger) = BuildMocks(LimitMode.CountOnly);
        var product = BuildProduct(new Money(100m, IRR));
        product.SetPurchaseLimit(TestValues.GroupId, 5);
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(product);
        var sut = new PurchaseLimitPolicy(systemSettings, groupRepo, productRepo, logger);
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await sut.GetCountLimitAsync(TestValues.ProductId, TestValues.GroupId, ct);

        // Assert
        await systemSettings.Received(1).GetLimitModeAsync(
            Arg.Is<CancellationToken>(t => t == ct));
        await productRepo.Received(1).GetByIdReadOnlyAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Common.Enums;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Users;
using TakOne.Infrastructure.Services;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for <see cref="SalaryBudgetService"/>.
///
/// COVERAGE APPROACH:
///   The service computes a monthly salary-budget snapshot for a customer:
///   1. look up the customer's user row to get GroupId (null for staff → null result)
///   2. look up the group → Salary (Money = amount + currency)
///   3. read system LimitMode — if CountOnly → null result
///   4. compute the Persian-month window via SalaryBudgetWindow
///   5. query the consumed amount in the window via ISaleRepository
///   6. return a SalaryBudgetInfo snapshot
/// </summary>
public class SalaryBudgetServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    private const string IRR = "IRR";
    private const string USD = "USD";

    // Builds a fully-wired mock environment. Default mode = SalaryOnly
    // (which means salary budget IS enforced). Tests override per-case.
    private static (
        IUserRepository userRepository,
        ICustomerGroupRepository groupRepository,
        ISaleRepository saleRepository,
        ISystemSettingsService systemSettings,
        ILogger<SalaryBudgetService> logger)
        BuildMocks(
            LimitMode mode = LimitMode.SalaryOnly)
    {
        var userRepo = Substitute.For<IUserRepository>();
        var groupRepo = Substitute.For<ICustomerGroupRepository>();
        var saleRepo = Substitute.For<ISaleRepository>();
        saleRepo.GetConsumedAmountForCustomerInWindowAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(0m);

        var systemSettings = Substitute.For<ISystemSettingsService>();
        systemSettings.GetLimitModeAsync(Arg.Any<CancellationToken>())
            .Returns(mode);

        var logger = Substitute.For<ILogger<SalaryBudgetService>>();
        return (userRepo, groupRepo, saleRepo, systemSettings, logger);
    }

    // Builds a real CustomerGroup with the supplied salary.
    private static CustomerGroup BuildGroup(Money salary)
        => CustomerGroup.Create("Test Group", salary);

    // ── customerId=Guid.Empty → null ───────────────────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_WithEmptyCustomerId_ReturnsNull()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) = BuildMocks();
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(Guid.Empty, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        // No repo calls — short-circuited before hitting the DB.
        await userRepo.DidNotReceive().GetByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── User not found → null ──────────────────────────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_WhenUserNotFound_ReturnsNull()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) = BuildMocks();
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        // Warning should be logged (defense-in-depth — the customer should
        // never disappear, but if they do, log it).
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── User found but GroupId null → null (staff) ─────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_WhenUserHasNoGroupId_ReturnsNull()
    {
        // Arrange
        // A staff user has GroupId = null → no salary, no budget.
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) = BuildMocks();
        var staffUser = User.CreateStaff("EMP-1", "Staff Person");
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(staffUser);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        // The group repo must NOT be called — the service short-circuits
        // once it sees the user is staff.
        await groupRepo.DidNotReceive().GetByIdReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── Group not found → null (with warning) ──────────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_WhenGroupNotFound_ReturnsNullAndLogsWarning()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) = BuildMocks();
        var customer = User.CreateCustomer("EMP-2", "Cust", TestValues.GroupId);
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(customer);
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((CustomerGroup?)null);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        // Defensive warning — User.GroupId is a FK, so the group should
        // always exist; if it's missing, that's a data-integrity issue.
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Mode=CountOnly → null (salary not enforced) ────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_WhenModeIsCountOnly_ReturnsNull()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) =
            BuildMocks(mode: LimitMode.CountOnly);
        var customer = User.CreateCustomer("EMP-3", "Cust", TestValues.GroupId);
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(customer);
        var group = BuildGroup(new Money(5000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        // Sale repo must NOT be called — salary isn't enforced, so the
        // consumed-amount query is skipped.
        await saleRepo.DidNotReceive().GetConsumedAmountForCustomerInWindowAsync(
            Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<CancellationToken>());
    }

    // ── Mode=SalaryOnly → returns SalaryBudgetInfo ─────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_WhenModeIsSalaryOnly_ReturnsBudgetInfo()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) =
            BuildMocks(mode: LimitMode.SalaryOnly);
        var customer = User.CreateCustomer("EMP-4", "Cust", TestValues.GroupId);
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(customer);
        var group = BuildGroup(new Money(5000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Salary.Amount.Should().Be(5000m);
        result.Salary.Currency.Should().Be(IRR);
        result.Consumed.Should().Be(0m);
        result.Remaining.Should().Be(5000m);
    }

    // ── Mode=Both → returns SalaryBudgetInfo ───────────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_WhenModeIsBoth_ReturnsBudgetInfo()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) =
            BuildMocks(mode: LimitMode.Both);
        var customer = User.CreateCustomer("EMP-5", "Cust", TestValues.GroupId);
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(customer);
        var group = BuildGroup(new Money(3000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Salary.Amount.Should().Be(3000m);
        result.Remaining.Should().Be(3000m);
    }

    // ── Consumed amount forwarded correctly ────────────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_WithConsumedAmount_ForwardsAmountFromSaleRepo()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) =
            BuildMocks(mode: LimitMode.SalaryOnly);
        var customer = User.CreateCustomer("EMP-6", "Cust", TestValues.GroupId);
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(customer);
        var group = BuildGroup(new Money(5000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        saleRepo.GetConsumedAmountForCustomerInWindowAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(1500m);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        result!.Consumed.Should().Be(1500m);
    }

    // ── Remaining = Salary.Amount - consumed ───────────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_ComputesRemainingAsSalaryMinusConsumed()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) =
            BuildMocks(mode: LimitMode.SalaryOnly);
        var customer = User.CreateCustomer("EMP-7", "Cust", TestValues.GroupId);
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(customer);
        var group = BuildGroup(new Money(4000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        saleRepo.GetConsumedAmountForCustomerInWindowAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(1200m);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        result!.Remaining.Should().Be(2800m); // 4000 - 1200
    }

    // ── Remaining can be NEGATIVE (salary lowered mid-month) ───────

    [Fact]
    public async Task GetBudgetInfoAsync_WhenConsumedExceedsSalary_RemainingIsNegative()
    {
        // Arrange
        // Salary was 5000; user spent 4000; admin lowered salary to 3000
        // mid-month → consumed (4000) > salary (3000) → remaining = -1000.
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) =
            BuildMocks(mode: LimitMode.SalaryOnly);
        var customer = User.CreateCustomer("EMP-8", "Cust", TestValues.GroupId);
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(customer);
        var group = BuildGroup(new Money(3000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        saleRepo.GetConsumedAmountForCustomerInWindowAsync(
                Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(4000m);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        result!.Remaining.Should().Be(-1000m);
    }

    // ── WindowStartUtc/WindowEndUtc come from SalaryBudgetWindow ──

    [Fact]
    public async Task GetBudgetInfoAsync_ReturnsWindowBoundariesFromSalaryBudgetWindow()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) =
            BuildMocks(mode: LimitMode.SalaryOnly);
        var customer = User.CreateCustomer("EMP-9", "Cust", TestValues.GroupId);
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(customer);
        var group = BuildGroup(new Money(5000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetBudgetInfoAsync(TestValues.CustomerId, CancellationToken.None);

        // Assert
        // The window boundaries are computed by SalaryBudgetWindow.GetStartOfCurrentMonth
        // and GetStartOfNextMonth — both pure static helpers. The values
        // change with the current Persian month, so we assert the property
        // types + that the start is before the end.
        result.Should().NotBeNull();
        result!.WindowStartUtc.Should().BeBefore(result.WindowEndUtc);
        result.WindowStartUtc.Kind.Should().Be(DateTimeKind.Utc);
        result.WindowEndUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    // ── GetGroupSalaryAsync(null) → null ───────────────────────────

    [Fact]
    public async Task GetGroupSalaryAsync_WithNullGroupId_ReturnsNull()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) = BuildMocks();
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetGroupSalaryAsync(null, CancellationToken.None);

        // Assert
        result.Should().BeNull();
        await groupRepo.DidNotReceive().GetByIdReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── GetGroupSalaryAsync with valid group → returns group.Salary ─

    [Fact]
    public async Task GetGroupSalaryAsync_WithValidGroupId_ReturnsGroupSalary()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) = BuildMocks();
        var salary = new Money(2500m, IRR);
        var group = BuildGroup(salary);
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetGroupSalaryAsync(TestValues.GroupId, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Amount.Should().Be(2500m);
        result.Currency.Should().Be(IRR);
    }

    // ── GetGroupSalaryAsync with missing group → null ──────────────

    [Fact]
    public async Task GetGroupSalaryAsync_WhenGroupNotFound_ReturnsNull()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) = BuildMocks();
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((CustomerGroup?)null);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);

        // Act
        var result = await sut.GetGroupSalaryAsync(TestValues.GroupId, CancellationToken.None);

        // Assert
        // group?.Salary — null when group is null. No exception thrown.
        result.Should().BeNull();
    }

    // ── CancellationToken forwarding ─────────────────────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_ForwardsCancellationTokenToAllRepoCalls()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) =
            BuildMocks(mode: LimitMode.SalaryOnly);
        var customer = User.CreateCustomer("EMP-10", "Cust", TestValues.GroupId);
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(customer);
        var group = BuildGroup(new Money(5000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await sut.GetBudgetInfoAsync(TestValues.CustomerId, ct);

        // Assert — the token is forwarded to each repo call.
        await userRepo.Received(1).GetByIdAsync(
            Arg.Any<Guid>(), Arg.Is<CancellationToken>(t => t == ct));
        await groupRepo.Received(1).GetByIdReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Is<CancellationToken>(t => t == ct));
        await systemSettings.Received(1).GetLimitModeAsync(
            Arg.Is<CancellationToken>(t => t == ct));
        await saleRepo.Received(1).GetConsumedAmountForCustomerInWindowAsync(
            Arg.Any<Guid>(),
            Arg.Any<DateTime>(),
            Arg.Any<DateTime>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task GetGroupSalaryAsync_ForwardsCancellationTokenToGroupRepo()
    {
        // Arrange
        var (userRepo, groupRepo, saleRepo, systemSettings, logger) = BuildMocks();
        var group = BuildGroup(new Money(5000m, IRR));
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(group);
        var sut = new SalaryBudgetService(userRepo, groupRepo, saleRepo, systemSettings, logger);
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await sut.GetGroupSalaryAsync(TestValues.GroupId, ct);

        // Assert
        await groupRepo.Received(1).GetByIdReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Is<CancellationToken>(t => t == ct));
    }
}

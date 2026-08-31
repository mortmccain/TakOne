using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Dashboard.Queries.GetDashboardStats;
using TakOne.Application.Dashboard.Specifications;
using TakOne.Application.Sales.Specifications;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Dashboard.Queries;

/// <summary>
/// Unit tests for <see cref="GetDashboardStatsQueryHandler"/>.
///
/// SECURITY FIX (Brutal Code Review v3 #03):
///   The query DTO no longer carries UserRoles/RequestedByUserId — the
///   handler resolves roles from ICurrentUserService.IsInRole (server-
///   verified claims). These tests configure the currentUser mock's
///   IsInRole return values instead of passing roles through the query.
///   This is strictly more realistic: it tests the actual code path a
///   real caller would exercise (claims-based role resolution, not a
///   client-supplied role list that could be spoofed).
///
/// ROUND 6 — FULL SCALARIZATION:
///   The handler now consumes ONLY the SQL-side aggregation methods on
///   ISaleRepository (the full-table load was deleted). Tests seed a
///   <see cref="FakeSaleRepository"/> — an in-memory double whose
///   aggregation semantics mirror the SQLite integration suite's contract
///   (half-open windows, COALESCE anchor, revenue-status filters, TOP-N
///   ordering) — and assert the handler's slicing/composition/conversion
///   logic on top of it. Scope selection (Employee vs Admin) is asserted
///   via the specs the fake records.
///
/// COVERAGE APPROACH:
///   • auth rejection (unauthenticated OR UserId=Guid.Empty) — no repo call
///   • customer role rejection — no repo call
///   • Employee role → every aggregation gets SaleByApproverSpecification
///   • Admin role → every aggregation gets AllSalesSpecification
///   • empty sales → all KPIs 0, TotalRevenue 0, TotalSalesCount 0
///   • IRR currency → IsToman=true, DisplayCurrency="تومان"
///   • non-IRR currency → IsToman=false
///   • Pending sale counted in revenue; Draft/Cancelled excluded
///   • status breakdown: one row per PRESENT status, descending, incl. Invoiced
///   • fixed-anchor daily KPIs (today/yesterday/this-month/last-month)
///   • weekly trend: 7+7 UTC-day buckets
///   • top products: top 7 by amount desc; period window re-anchors the card
///   • top employees: rank 1..4; period window re-anchors the card
///   • recent orders sorted by SubmittedAtUtc desc + take 6
///   • oldest pending age minutes
///   • period selector: KPIs vs previous window; chart series = Tehran-day
///     buckets of the window; degenerate window = zeros without throwing
///   • userRepository.GetActiveCustomerCountAsync throws → ActiveCustomersCount=0
/// </summary>
public class GetDashboardStatsQueryHandlerTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    private const string IRR = "IRR";
    private const string USD = "USD";

    /// <summary>Tehran's fixed UTC offset — matches the handler + razor.</summary>
    private static readonly TimeSpan TehranUtcOffset = TimeSpan.FromHours(3.5);

    // Builds a fully-wired mock environment around a seeded (or empty)
    // FakeSaleRepository. Roles are configured on the currentUser mock
    // via IsInRole — NOT passed through the query DTO (which no longer
    // carries UserRoles). This mirrors the production handler, which
    // reads currentUser.IsInRole (server-verified claims).
    private static (
        ICurrentUserService currentUser,
        FakeSaleRepository saleRepository,
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        ILogger<GetDashboardStatsQueryHandler> logger)
        BuildMocks(bool authenticated, params string[] roles)
        => BuildMocksCore(authenticated, userId: null, fullName: "Test User", roles);

    private static (
        ICurrentUserService currentUser,
        FakeSaleRepository saleRepository,
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        ILogger<GetDashboardStatsQueryHandler> logger)
        BuildMocks(bool authenticated, Guid? userId, params string[] roles)
        => BuildMocksCore(authenticated, userId, fullName: "Test User", roles);

    private static (
        ICurrentUserService currentUser,
        FakeSaleRepository saleRepository,
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        ILogger<GetDashboardStatsQueryHandler> logger)
        BuildMocksCore(bool authenticated, Guid? userId, string fullName, string[] roles)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(authenticated);
        currentUser.UserId.Returns(userId ?? TestValues.CreatedByUserId);
        currentUser.FullName.Returns(fullName);

        // Wire up IsInRole for each supplied role. Unconfigured roles
        // return false (NSubstitute default for bool returns).
        foreach (var role in roles)
        {
            currentUser.IsInRole(role).Returns(true);
        }

        var saleRepo = new FakeSaleRepository();

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Categories.Entities.Category>());

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetActiveCustomerCountAsync(Arg.Any<CancellationToken>())
            .Returns(0);

        var logger = Substitute.For<ILogger<GetDashboardStatsQueryHandler>>();

        return (currentUser, saleRepo, categoryRepo, userRepo, logger);
    }

    // Builds a query. The query carries NO caller-identity fields — the
    // handler resolves roles from ICurrentUserService (server-verified
    // claims). See Brutal Code Review v3 finding #03.
    private static GetDashboardStatsQuery BuildQuery()
        => new();

    // Builds a Sale in Pending status with one line item at the given
    // unit price. SubmittedAtUtc is set to now so the dashboard's
    // "today" / "this month" counts include it.
    private static Sale BuildPendingSale(Money unitPrice, int quantity = 1)
    {
        var sale = Sale.Create(
            customerId: TestValues.CustomerId,
            customerName: "C1",
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: "Creator");
        sale.AddLineItem(
            productId: TestValues.ProductId,
            productName: "Widget",
            quantity: quantity,
            unitPrice: unitPrice);
        sale.Submit(SaleNumber.Create(1403, 1));
        return sale;
    }

    // Builds a Sale in Draft status with one line item — not submitted.
    private static Sale BuildDraftSale(Money unitPrice)
    {
        var sale = Sale.Create(
            customerId: TestValues.CustomerId,
            customerName: "C1",
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: "Creator");
        sale.AddLineItem(
            productId: TestValues.ProductId,
            productName: "Widget",
            quantity: 1,
            unitPrice: unitPrice);
        return sale;
    }

    // Builds a Sale that's Pending → Approved → Cancelled.
    private static Sale BuildCancelledSale(Money unitPrice)
    {
        var sale = Sale.Create(
            customerId: TestValues.CustomerId,
            customerName: "C1",
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: "Creator");
        sale.AddLineItem(
            productId: TestValues.ProductId,
            productName: "Widget",
            quantity: 1,
            unitPrice: unitPrice);
        sale.Submit(SaleNumber.Create(1403, 2));
        sale.Approve(TestValues.ApprovedByUserId);
        sale.Cancel(TestValues.CancelledByUserId, "Customer changed mind");
        return sale;
    }

    /// <summary>
    /// Writes a Sale's SubmittedAtUtc via reflection — the dashboard's
    /// KPI date filters key on (SubmittedAtUtc ?? CreatedAtUtc), and the
    /// aggregate API sets it to UtcNow inside Submit(). Test-only; the
    /// domain API stays immutable in production code.
    /// </summary>
    private static void SetSubmittedAt(Sale sale, DateTime utc)
    {
        typeof(Sale).GetProperty(nameof(Sale.SubmittedAtUtc))!
            .SetValue(sale, utc);
    }

    /// <summary>
    /// The Tehran-local midnight of a UTC instant, expressed back as a
    /// UTC instant (localDate − 03:30) — the same contract as
    /// Dashboard.razor's ToUtcInstant. Used to build the
    /// Tehran-midnight-aligned period windows the page's presets send.
    /// </summary>
    private static DateTime TehranMidnightUtc(DateTime utcInstant)
        => DateTime.SpecifyKind((utcInstant + TehranUtcOffset).Date, DateTimeKind.Utc) - TehranUtcOffset;

    // ── Auth rejection ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenUnauthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: false, Roles.Admin);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        // Auth reject short-circuits BEFORE any DB call (Round 6: the
        // fake counts every aggregation call).
        saleRepo.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        // IsAuthenticated=true but UserId=Guid.Empty — the second branch of
        // the auth check rejects it (defense-in-depth).
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, userId: Guid.Empty, Roles.Admin);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
    }

    [Fact]
    public async Task HandleAsync_WhenUnauthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: false, Roles.Admin);
        var query = BuildQuery();

        // Act
        await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Customer role rejection ─────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenUserIsCustomer_ReturnsAccessDenied()
    {
        // Arrange
        // Customer role must be rejected — they don't have a dashboard.
        // NOTE: only Roles.Customer is wired on the mock — the handler's
        // currentUser.IsInRole(Roles.Customer) returns true, all other
        // roles return false. This is the exact shape a spoofing
        // attacker would NOT be able to produce (they can't set server
        // claims), proving the spoofing hole is closed.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Customer);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Access denied: customers do not have a dashboard.");
        saleRepo.CallCount.Should().Be(0);
    }

    // ── Employee role → uses SaleByApproverSpecification ───────────

    [Fact]
    public async Task HandleAsync_WhenUserIsEmployee_UsesSaleByApproverSpecification()
    {
        // Arrange
        // An Employee (no Admin/Manager) sees only sales they approved.
        // Round 6: the handler passes the SAME scope spec to EVERY
        // aggregation method — the fake records them all.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Employee);
        var query = BuildQuery();

        // Act
        await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert — every recorded spec is the approver scope.
        saleRepo.ReceivedSpecs.Should().NotBeEmpty();
        saleRepo.ReceivedSpecs.Should().OnlyContain(
            spec => spec is SaleByApproverSpecification);
    }

    [Fact]
    public async Task HandleAsync_WhenUserIsEmployee_SetsIsEmployeeScopedTrue()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Employee);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsEmployeeScoped.Should().BeTrue();
    }

    // ── Admin role → uses AllSalesSpecification ────────────────────

    [Fact]
    public async Task HandleAsync_WhenUserIsAdmin_UsesAllSalesSpecification()
    {
        // Arrange
        // Admin → company-wide overview, no approver filter.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var query = BuildQuery();

        // Act
        await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        saleRepo.ReceivedSpecs.Should().NotBeEmpty();
        saleRepo.ReceivedSpecs.Should().OnlyContain(
            spec => spec is AllSalesSpecification);
    }

    [Fact]
    public async Task HandleAsync_WhenUserIsAdmin_SetsIsEmployeeScopedFalse()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsEmployeeScoped.Should().BeFalse();
    }

    // ── Empty sales → all KPIs 0 ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithNoSales_AllKpisAreZero()
    {
        // Arrange — an unseeded fake (no sales anywhere).
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var dto = result.Value;
        dto.TotalSalesCount.Should().Be(0);
        dto.DraftSalesCount.Should().Be(0);
        dto.PendingSalesCount.Should().Be(0);
        dto.ApprovedSalesCount.Should().Be(0);
        dto.CancelledSalesCount.Should().Be(0);
        dto.TotalRevenue.Should().Be(0);
        dto.StatusBreakdown.Should().BeEmpty();
        dto.TopProducts.Should().BeEmpty();
        dto.TopEmployees.Should().BeEmpty();
        dto.RecentOrders.Should().BeEmpty();
        dto.OldestPendingSaleAgeMinutes.Should().BeNull();
    }

    // ── IRR currency → IsToman=true ────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithIrrSale_SetsIsTomanTrueAndDisplayCurrencyToToman()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        // A pending sale in IRR currency — the handler detects the
        // display currency from the recent-sales slice (Round 6) and
        // sums revenue server-side from the same seed.
        saleRepo.Seed(BuildPendingSale(new Money(1000m, IRR)));
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.Value.IsToman.Should().BeTrue();
        result.Value.DisplayCurrency.Should().Be("تومان");
        // 1000 IRR ÷ 10 = 100 Toman.
        result.Value.TotalRevenue.Should().Be(100m);
    }

    // ── Non-IRR currency → IsToman=false ───────────────────────────

    [Fact]
    public async Task HandleAsync_WithUsdSale_SetsIsTomanFalseAndDisplayCurrencyToUsd()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        saleRepo.Seed(BuildPendingSale(new Money(100m, USD)));
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.Value.IsToman.Should().BeFalse();
        result.Value.DisplayCurrency.Should().Be(USD);
        // No ÷10 conversion — amount stays at 100.
        result.Value.TotalRevenue.Should().Be(100m);
    }

    // ── Pending sale counted in revenueEligibleSales ───────────────

    [Fact]
    public async Task HandleAsync_WithPendingSale_IncludesInTotalRevenue()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        saleRepo.Seed(BuildPendingSale(new Money(2000m, IRR))); // 200 Toman after ÷10.
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.Value.TotalRevenue.Should().Be(200m);
        result.Value.PendingSalesCount.Should().Be(1);
        result.Value.TotalSalesCount.Should().Be(1);
    }

    // ── Draft sale excluded from revenueEligibleSales ──────────────

    [Fact]
    public async Task HandleAsync_WithDraftSale_ExcludesFromTotalRevenue()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        saleRepo.Seed(BuildDraftSale(new Money(2000m, IRR)));
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        // Draft → excluded from revenueEligibleSales → TotalRevenue=0.
        result.Value.TotalRevenue.Should().Be(0);
        // But TotalSalesCount counts ALL sales (including Draft).
        result.Value.TotalSalesCount.Should().Be(1);
        result.Value.DraftSalesCount.Should().Be(1);
        // The draft's recent-slice presence drives currency detection —
        // a draft with an IRR total still yields IRR/Toman display.
        result.Value.IsToman.Should().BeTrue();
    }

    // ── Cancelled sale excluded from revenueEligibleSales ──────────

    [Fact]
    public async Task HandleAsync_WithCancelledSale_ExcludesFromTotalRevenue()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        saleRepo.Seed(BuildCancelledSale(new Money(2000m, IRR)));
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.Value.TotalRevenue.Should().Be(0);
        result.Value.CancelledSalesCount.Should().Be(1);
        // Cancelled sales don't count in this-month's submitted totals
        // (the revenue-status filter excludes them).
        result.Value.ThisMonthEmployeePurchaseTotal.Should().Be(0);
    }

    // ── Status breakdown: present statuses, descending, incl. Invoiced ──

    [Fact]
    public async Task HandleAsync_StatusBreakdown_IncludesInvoicedAndOrdersDescending()
    {
        // Arrange — 2 pending, 1 approved, 3 invoiced, 1 cancelled.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);

        Sale MakeInvoiced(decimal amount)
        {
            var sale = BuildPendingSale(new Money(amount, IRR));
            sale.Approve(TestValues.ApprovedByUserId);
            sale.MarkAsInvoiced(TestValues.InvoicedByUserId);
            return sale;
        }

        saleRepo.Seed(new[]
        {
            BuildPendingSale(new Money(10m, IRR)),
            BuildPendingSale(new Money(10m, IRR)),
            BuildPendingSale(new Money(10m, IRR)).AlsoApprove(),
            MakeInvoiced(20m),
            MakeInvoiced(20m),
            MakeInvoiced(20m),
            BuildCancelledSale(new Money(30m, IRR)),
        });

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            BuildQuery(), currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert — one row per present status, Invoiced INCLUDED (the
        // old per-status COUNTs never fed it), counts descending (ties
        // between equal counts have no guaranteed order — by-count is
        // the contract).
        var breakdown = result.Value.StatusBreakdown;
        breakdown.Should().HaveCount(4);
        breakdown.Select(s => s.Count).Should().BeInDescendingOrder();
        breakdown.Single(s => s.Status == nameof(SaleStatus.Invoiced)).Count.Should().Be(3);
        breakdown.Single(s => s.Status == nameof(SaleStatus.Pending)).Count.Should().Be(2);
        breakdown.Single(s => s.Status == nameof(SaleStatus.Approved)).Count.Should().Be(1);
        breakdown.Single(s => s.Status == nameof(SaleStatus.Cancelled)).Count.Should().Be(1);
        // The donut center total = sum of all statuses.
        breakdown.Sum(s => s.Count).Should().Be(7);
    }

    // ── Top products sorted by TotalAmount desc + take top 7 ───────

    [Fact]
    public async Task HandleAsync_WithMultipleProducts_TakesTop7ByTotalAmountDesc()
    {
        // Arrange
        // Build 8 pending sales, each with ONE line item for a distinct
        // product name. The handler's TopProducts takes the SQL GROUP
        // BY's top 7 by amount. The 8th product must be absent.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var sales = new List<Sale>();
        for (var i = 0; i < 8; i++)
        {
            // Amounts: 100, 200, ..., 800 IRR — the aggregation sums
            // Quantity × UnitPrice per line. After ÷10 they become 10,
            // 20, ..., 80 Toman. The product with the LOWEST amount
            // (i=0) gets dropped from the top-7.
            var sale = Sale.Create(
                customerId: TestValues.CustomerId,
                customerName: $"C{i}",
                createdByUserId: TestValues.CreatedByUserId,
                createdByName: "Creator");
            sale.AddLineItem(
                productId: Guid.NewGuid(),
                productName: $"Product{i}",
                quantity: 1,
                unitPrice: new Money((i + 1) * 100m, IRR));
            sale.Submit(SaleNumber.Create(1403, i + 10));
            sales.Add(sale);
        }
        saleRepo.Seed(sales);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.Value.TopProducts.Count.Should().Be(7);
        // The dropped product is Product0 (amount 100 → 10 Toman — lowest).
        result.Value.TopProducts.Should().NotContain(p => p.ProductName == "Product0");
        // The retained products are Product1..Product7.
        result.Value.TopProducts.Select(p => p.ProductName).Should()
            .BeEquivalentTo(new[] { "Product1", "Product2", "Product3", "Product4", "Product5", "Product6", "Product7" });
        // The list must be ordered descending by TotalAmount.
        result.Value.TopProducts.Select(p => p.TotalAmount).Should()
            .BeInDescendingOrder();
        // IRR→Toman conversion applies to the aggregated row amounts.
        result.Value.TopProducts.Single(p => p.ProductName == "Product7").TotalAmount
            .Should().Be(80m, "800 IRR ÷ 10");
    }

    // ── Recent orders sorted by SubmittedAtUtc desc + take 6 ───────

    [Fact]
    public async Task HandleAsync_WithManySales_RecentOrdersTakesLatest6()
    {
        // Arrange
        // Build 8 sales, each submitted at a slightly different time
        // (we sleep 15ms between each). The handler should return the
        // 6 most-recent in descending order. Round 6: the bounded slice
        // comes straight from the (fake) repo's TOP 6 — the handler
        // just projects it; the fake computes the same TOP 6 the SQL
        // ORDER BY ... DESC LIMIT 6 produces.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var sales = new List<Sale>();
        for (var i = 0; i < 8; i++)
        {
            var sale = Sale.Create(
                customerId: TestValues.CustomerId,
                customerName: $"C{i}",
                createdByUserId: TestValues.CreatedByUserId,
                createdByName: "Creator");
            sale.AddLineItem(
                productId: Guid.NewGuid(),
                productName: $"Product{i}",
                quantity: 1,
                unitPrice: new Money(1000m, IRR));
            sale.Submit(SaleNumber.Create(1403, i + 100));
            // Sleep ~15ms to ensure distinct SubmittedAtUtc timestamps.
            Thread.Sleep(15);
            sales.Add(sale);
        }
        saleRepo.Seed(sales);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.Value.RecentOrders.Count.Should().Be(6);
        // The most-recent 6 sales are C7, C6, ..., C2 (since we submitted
        // C0..C7 in time order).
        result.Value.RecentOrders.Select(o => o.CustomerName).Should()
            .BeInDescendingOrder(); // not by name — but the order is reverse-chronological
        // CustomerName "C7" should be first (most recent).
        result.Value.RecentOrders.First().CustomerName.Should().Be("C7");
        // "C0" and "C1" (the two oldest) must be absent.
        result.Value.RecentOrders.Select(o => o.CustomerName).Should()
            .NotContain(new[] { "C0", "C1" });
    }

    // ── Round 4: fixed-anchor KPI trend-delta computations ──────────

    [Fact]
    public async Task HandleAsync_ComputesPreviousPeriodKpiValues()
    {
        // Arrange — sales at three well-separated instants: today,
        // yesterday, and five days into last month.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);

        var today = DateTime.UtcNow.Date;
        var thisMonthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonthStart = thisMonthStart.AddMonths(-1);

        var todaySale = BuildPendingSale(new Money(100m, "IRR"));
        SetSubmittedAt(todaySale, today.AddHours(1));

        var yesterdaySale = BuildPendingSale(new Money(60m, "IRR"));
        SetSubmittedAt(yesterdaySale, today.AddDays(-1).AddHours(1));

        var lastMonthSale = BuildPendingSale(new Money(200m, "IRR"));
        // Five days into last month — provably inside [lastMonthStart,
        // thisMonthStart) for every calendar month, and provably never
        // equal to yesterday (yesterday is either in this month or is a
        // different last-month day than the 6th).
        SetSubmittedAt(lastMonthSale, lastMonthStart.AddDays(5).AddHours(1));

        saleRepo.Seed(new List<Sale> { todaySale, yesterdaySale, lastMonthSale });

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            BuildQuery(), currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert — today vs yesterday (counts, status-filtered).
        result.Value.TodayOrdersCount.Should().Be(1);
        result.Value.YesterdayOrdersCount.Should().Be(1,
            "only the yesterday-submitted sale counts — the today and last-month sales don't");

        // This month vs last month (amounts, display currency = IRR/10).
        // MONTH-BOUNDARY NOTE: when today is the 1st, yesterday falls in
        // LAST month and joins its total — the expected value folds that
        // in so the test is deterministic on every day of the year.
        var yesterdayFallsInLastMonth = today.AddDays(-1) < thisMonthStart;
        var expectedLastMonthRaw = 200m + (yesterdayFallsInLastMonth ? 60m : 0m);

        result.Value.LastMonthEmployeePurchaseTotal.Should().Be(expectedLastMonthRaw / 10m,
            "IRR amounts display as Toman (÷10); yesterday joins the last-month total only when today is the 1st");

        // Approved/invoiced counts: every seeded sale is Pending → the
        // approved/invoiced deltas are zero on both sides.
        result.Value.LastMonthApprovedSalesCount.Should().Be(0);
        result.Value.LastMonthInvoicedSalesCount.Should().Be(0);
    }

    // ── ROUND 5/6 — period selector (the FromUtc/ToUtc window) ───────

    [Fact]
    public async Task HandleAsync_WithoutPeriodWindow_PeriodFieldsAreZeroAndNotScoped()
    {
        // The default query (no window) keeps the legacy fixed-anchor
        // behavior — every Period* field stays zero and IsPeriodScoped is
        // false. This is the back-compat contract for every existing
        // caller.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);
        saleRepo.Seed(BuildPendingSale(new Money(100m, "IRR")));

        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            BuildQuery(), currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        result.Value.IsPeriodScoped.Should().BeFalse();
        result.Value.PeriodFromUtc.Should().BeNull();
        result.Value.PeriodToUtc.Should().BeNull();
        result.Value.PeriodOrdersCount.Should().Be(0);
        result.Value.PreviousPeriodOrdersCount.Should().Be(0);
        result.Value.PeriodEmployeePurchaseTotal.Should().Be(0m);
        result.Value.PreviousPeriodEmployeePurchaseTotal.Should().Be(0m);
        result.Value.PeriodApprovedSalesCount.Should().Be(0);
        result.Value.PeriodInvoicedSalesCount.Should().Be(0);
        // The fixed-anchor fields still work.
        result.Value.TodayOrdersCount.Should().Be(1);
        // The fixed-anchor weekly trend renders 7+7 UTC-day points.
        result.Value.ThisWeekRevenue.Should().HaveCount(7);
        result.Value.LastWeekRevenue.Should().HaveCount(7);
        // Today's bucket carries the seeded sale's amount (100 IRR → 10).
        result.Value.ThisWeekRevenue.Last().TotalAmount.Should().Be(10m);
    }

    [Fact]
    public async Task HandleAsync_WithPeriodWindow_ComputesPeriodKpisVsPreviousWindow()
    {
        // Window [now-10d, now); previous window [now-20d, now-10d). Seeded
        // sales: two in the window, one in the previous window, one far
        // outside both. Also verifies the window echo fields.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);

        var now = DateTime.UtcNow;
        var from = now.AddDays(-10);
        var to = now.AddMinutes(5);

        var inWindowSale1 = BuildPendingSale(new Money(100m, "IRR"));
        SetSubmittedAt(inWindowSale1, now.AddHours(-2));
        var inWindowSale2 = BuildPendingSale(new Money(50m, "IRR"));
        SetSubmittedAt(inWindowSale2, from.AddHours(1)); // just inside the inclusive lower bound
        var previousWindowSale = BuildPendingSale(new Money(200m, "IRR"));
        SetSubmittedAt(previousWindowSale, from.AddHours(-1)); // just before the window → previous window
        var outsideSale = BuildPendingSale(new Money(999m, "IRR"));
        SetSubmittedAt(outsideSale, now.AddDays(-90));

        saleRepo.Seed(new List<Sale> { inWindowSale1, inWindowSale2, previousWindowSale, outsideSale });

        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            new GetDashboardStatsQuery { FromUtc = from, ToUtc = to },
            currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        result.Value.IsPeriodScoped.Should().BeTrue();
        result.Value.PeriodFromUtc.Should().Be(from);
        result.Value.PeriodToUtc.Should().Be(to);

        result.Value.PeriodOrdersCount.Should().Be(2,
            "the two in-window sales; the previous-window and far-outside sales don't count");
        result.Value.PreviousPeriodOrdersCount.Should().Be(1);

        result.Value.PeriodEmployeePurchaseTotal.Should().Be(150m / 10m,
            "100 + 50 IRR displayed as Toman (÷10); the previous-window and outside sales don't count");
        result.Value.PreviousPeriodEmployeePurchaseTotal.Should().Be(200m / 10m);
    }

    [Fact]
    public async Task HandleAsync_WithPeriodWindow_StatusFiltersMirrorFixedAnchors()
    {
        // The period KPIs apply the SAME status filters as their
        // fixed-anchor counterparts: orders/purchase exclude Cancelled;
        // approved/invoiced cards count only their own statuses.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);

        var now = DateTime.UtcNow;
        var from = now.AddDays(-7);
        var to = now.AddMinutes(5);

        var cancelled = BuildCancelledSale(new Money(100m, "IRR"));
        SetSubmittedAt(cancelled, now.AddHours(-1));

        var approved = BuildPendingSale(new Money(80m, "IRR"));
        approved.Approve(TestValues.ApprovedByUserId);
        SetSubmittedAt(approved, now.AddHours(-2));

        var invoiced = BuildPendingSale(new Money(60m, "IRR"));
        invoiced.Approve(TestValues.ApprovedByUserId);
        invoiced.MarkAsInvoiced(TestValues.ApprovedByUserId);
        SetSubmittedAt(invoiced, now.AddHours(-3));

        var draft = BuildDraftSale(new Money(40m, "IRR")); // never submitted → excluded everywhere

        saleRepo.Seed(new List<Sale> { cancelled, approved, invoiced, draft });

        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            new GetDashboardStatsQuery { FromUtc = from, ToUtc = to },
            currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        result.Value.PeriodOrdersCount.Should().Be(2,
            "approved + invoiced count; cancelled and draft are excluded");
        result.Value.PeriodEmployeePurchaseTotal.Should().Be(140m / 10m,
            "approved (80) + invoiced (60); cancelled and draft excluded");
        result.Value.PeriodApprovedSalesCount.Should().Be(1);
        result.Value.PeriodInvoicedSalesCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleAsync_WithPeriodWindowOpenEnded_TreatsNullToUtcAsNow()
    {
        // FromUtc set + ToUtc null → the window is [from, now): a sale
        // submitted an hour ago counts, one before the bound doesn't.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);

        var now = DateTime.UtcNow;
        var from = now.AddDays(-7);

        var recent = BuildPendingSale(new Money(10m, "IRR"));
        SetSubmittedAt(recent, now.AddHours(-1));
        var tooOld = BuildPendingSale(new Money(20m, "IRR"));
        SetSubmittedAt(tooOld, from.AddDays(-1));

        saleRepo.Seed(new List<Sale> { recent, tooOld });

        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            new GetDashboardStatsQuery { FromUtc = from },
            currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        result.Value.IsPeriodScoped.Should().BeTrue();
        result.Value.PeriodToUtc.Should().BeNull("an open-ended window echoes back as null");
        result.Value.PeriodOrdersCount.Should().Be(1);
        result.Value.PreviousPeriodOrdersCount.Should().Be(1,
            "the previous equal-length window [from-7d, from) holds the too-old sale");
    }

    [Fact]
    public async Task HandleAsync_WithInvertedPeriodWindow_YieldsZerosWithoutThrowing()
    {
        // Degenerate/inverted windows never throw — same semantics as the
        // sales list's date filter.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);

        var now = DateTime.UtcNow;
        saleRepo.Seed(BuildPendingSale(new Money(100m, "IRR")));

        var act = () => GetDashboardStatsQueryHandler.HandleAsync(
            new GetDashboardStatsQuery { FromUtc = now.AddDays(-1), ToUtc = now.AddDays(-7) },
            currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        var result = await act();
        result.IsSuccess.Should().BeTrue("an inverted window is degenerate, not an error");
        result.Value.IsPeriodScoped.Should().BeTrue();
        result.Value.PeriodOrdersCount.Should().Be(0);
        result.Value.PreviousPeriodOrdersCount.Should().Be(0);
        result.Value.PeriodEmployeePurchaseTotal.Should().Be(0m);
        // The chart series collapse to empty lists (no days in the window).
        result.Value.ThisWeekRevenue.Should().BeEmpty();
        result.Value.LastWeekRevenue.Should().BeEmpty();
    }

    // ── ROUND 6 — period-driven chart series (Tehran-day buckets) ────

    [Fact]
    public async Task HandleAsync_WithPeriodWindow_ChartSeriesBucketTheWindowByTehranDay()
    {
        // A 3-day Tehran-aligned window (the shape every razor preset
        // sends): the chart series must carry one point per Tehran DAY
        // of the window, plus the equal-length preceding window.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);

        var now = DateTime.UtcNow;
        var from = TehranMidnightUtc(now);
        var to = from.AddDays(3);

        // Day 1 (Tehran): one sale at 10:00Z (= 13:30 Tehran).
        var day1Sale = BuildPendingSale(new Money(100m, "IRR"));
        SetSubmittedAt(day1Sale, from.AddHours(10));

        // Day 2 (Tehran): a sale at 19:00 Tehran (15:30Z)…
        var day2Evening = BuildPendingSale(new Money(60m, "IRR"));
        SetSubmittedAt(day2Evening, from.AddDays(1).AddHours(19));
        // …and one at 23:00Z on day 2 — that's 02:30 Tehran on DAY 3.
        var day3EarlyTehran = BuildPendingSale(new Money(40m, "IRR"));
        SetSubmittedAt(day3EarlyTehran, from.AddDays(2).AddHours(2).AddMinutes(30));

        // Previous window: one sale on the FIRST day of the previous
        // window (3 days before `from`, Tehran).
        var previousWindowSale = BuildPendingSale(new Money(500m, "IRR"));
        SetSubmittedAt(previousWindowSale, from.AddDays(-3).AddHours(5));

        saleRepo.Seed(new List<Sale>
        {
            day1Sale, day2Evening, day3EarlyTehran, previousWindowSale
        });

        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            new GetDashboardStatsQuery { FromUtc = from, ToUtc = to },
            currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // The period series has one point per Tehran day (3), each
        // labelled with the weekday (≤14 points → "ddd").
        result.Value.ThisWeekRevenue.Should().HaveCount(3);
        result.Value.LastWeekRevenue.Should().HaveCount(3,
            "the preceding equal-length window has the same point count → the chart stays index-aligned");

        // Values: day1 = 100, day2 = 60, day3 = 40 (IRR → ÷10 Toman).
        result.Value.ThisWeekRevenue[0].TotalAmount.Should().Be(10m);
        result.Value.ThisWeekRevenue[1].TotalAmount.Should().Be(6m);
        result.Value.ThisWeekRevenue[2].TotalAmount.Should().Be(4m,
            "the 23:00Z sale belongs to the NEXT Tehran day (02:30) — Tehran buckets, not UTC");

        // The chart totals agree with the period KPI to the rial.
        result.Value.ThisWeekRevenue.Sum(d => d.TotalAmount).Should()
            .Be(result.Value.PeriodEmployeePurchaseTotal);

        // The previous window's series carries its sale on the matching
        // day (day 0 of the previous window = 4 days before `from`).
        result.Value.LastWeekRevenue[0].TotalAmount.Should().Be(50m);
        result.Value.LastWeekRevenue.Skip(1).Should().OnlyContain(d => d.TotalAmount == 0m);
    }

    // ── ROUND 6 — period-driven top-N cards ──────────────────────────

    [Fact]
    public async Task HandleAsync_WithPeriodWindow_TopProductsAndEmployeesReAnchorToWindow()
    {
        // In period mode the top-products card drops sales outside the
        // window (the fixed-mode card would use last-30-days), and the
        // top-employees card re-anchors from "this month" to the window.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);

        var now = DateTime.UtcNow;
        var from = now.AddDays(-5);
        var to = now.AddMinutes(5);

        // IN window: Alice buys the "Inside" product.
        var insideSale = Sale.Create(TestValues.CustomerId, "Alice",
            TestValues.CreatedByUserId, "Creator");
        insideSale.AddLineItem(Guid.NewGuid(), "Inside", 2, new Money(100m, IRR));
        insideSale.Submit(SaleNumber.Create(1405, 71));
        SetSubmittedAt(insideSale, now.AddHours(-2));

        // OUT of window (three weeks old): Bob buys the "Outside" product
        // with a bigger amount — it must NOT appear in either card.
        var outsideSale = Sale.Create(
            Guid.Parse("deadbeef-dead-beef-dead-beefdeadbeef"), "Bob",
            TestValues.CreatedByUserId, "Creator");
        outsideSale.AddLineItem(Guid.NewGuid(), "Outside", 5, new Money(500m, IRR));
        outsideSale.Submit(SaleNumber.Create(1405, 72));
        SetSubmittedAt(outsideSale, now.AddDays(-21));

        saleRepo.Seed(new List<Sale> { insideSale, outsideSale });

        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            new GetDashboardStatsQuery { FromUtc = from, ToUtc = to },
            currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        var product = result.Value.TopProducts.Should().ContainSingle().Which;
        product.ProductName.Should().Be("Inside");
        product.QuantitySold.Should().Be(2);
        product.TotalAmount.Should().Be(20m, "2 × 100 IRR ÷ 10");

        var employee = result.Value.TopEmployees.Should().ContainSingle().Which;
        employee.FullName.Should().Be("Alice");
        employee.Rank.Should().Be(1);
        employee.TotalAmount.Should().Be(20m);
    }

    [Fact]
    public async Task HandleAsync_TopEmployees_AssignsRanksByAmountDescending()
    {
        // Fixed mode (no window): the card covers the current month; the
        // ranks follow the amount ordering.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);

        var carolId = Guid.Parse("feedfeed-feed-feed-feed-feedfeedfeed");

        var aliceSale = Sale.Create(TestValues.CustomerId, "Alice",
            TestValues.CreatedByUserId, "Creator");
        aliceSale.AddLineItem(Guid.NewGuid(), "P", 1, new Money(300m, IRR));
        aliceSale.Submit(SaleNumber.Create(1405, 81));
        SetSubmittedAt(aliceSale, DateTime.UtcNow.AddHours(-3));

        var bobSale = Sale.Create(
            Guid.Parse("deadbeef-dead-beef-dead-beefdeadbeef"), "Bob",
            TestValues.CreatedByUserId, "Creator");
        bobSale.AddLineItem(Guid.NewGuid(), "P", 1, new Money(700m, IRR));
        bobSale.Submit(SaleNumber.Create(1405, 82));
        SetSubmittedAt(bobSale, DateTime.UtcNow.AddHours(-2));

        var carolSale = Sale.Create(carolId, "Carol",
            TestValues.CreatedByUserId, "Creator");
        carolSale.AddLineItem(Guid.NewGuid(), "P", 1, new Money(100m, IRR));
        carolSale.Submit(SaleNumber.Create(1405, 83));
        SetSubmittedAt(carolSale, DateTime.UtcNow.AddHours(-1));

        saleRepo.Seed(new List<Sale> { aliceSale, bobSale, carolSale });

        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            BuildQuery(), currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        result.Value.TopEmployees.Select(e => (e.FullName, e.Rank)).Should()
            .ContainInOrder(("Bob", 1), ("Alice", 2), ("Carol", 3));
        // Amounts are display-converted once (IRR ÷ 10).
        result.Value.TopEmployees[0].TotalAmount.Should().Be(70m);
    }

    // ── ROUND 6 — oldest pending age + monthly chart ─────────────────

    [Fact]
    public async Task HandleAsync_OldestPendingSaleAge_ReflectsTheOldestPendingAnchor()
    {
        var (currentUser, saleRepo, categoryRepo, userRepo, logger) =
            BuildMocks(authenticated: true, Roles.Admin);

        var oldest = BuildPendingSale(new Money(10m, IRR));
        SetSubmittedAt(oldest, DateTime.UtcNow.AddHours(-3));
        var newest = BuildPendingSale(new Money(20m, IRR));
        SetSubmittedAt(newest, DateTime.UtcNow.AddMinutes(-10));

        saleRepo.Seed(new List<Sale> { oldest, newest });

        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            BuildQuery(), currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        result.Value.OldestPendingSaleAgeMinutes.Should().BeGreaterThan(170)
            .And.BeLessThan(190, "≈3 hours (the older of the two pending anchors)");

        // The monthly chart carries the seeded sales in the current month.
        var now = DateTime.UtcNow;
        result.Value.CurrentYearMonthlyData[now.Month - 1].TotalAmount
            .Should().Be(3m, "(10 + 20) IRR ÷ 10 — both sales anchor this month");
    }

    // ── userRepository.GetActiveCustomerCountAsync throws → defaults to 0

    [Fact]
    public async Task HandleAsync_WhenUserRepoThrowsActiveCustomerCount_DefaultsToZero()
    {
        // Arrange
        // userRepo throws — handler catches and defaults ActiveCustomersCount to 0.
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        userRepo.GetActiveCustomerCountAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("DB down")));
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        // The handler must NOT propagate the exception — it returns Success
        // with ActiveCustomersCount=0.
        result.IsSuccess.Should().BeTrue();
        result.Value.ActiveCustomersCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenUserRepoThrows_LogsWarning()
    {
        // Arrange
        var (currentUser, saleRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        userRepo.GetActiveCustomerCountAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("DB down")));
        var query = BuildQuery();

        // Act
        await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        // The handler should log a warning (with the exception attached).
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }
}

/// <summary>
/// Test-only extension: approve a pending sale in one expression
/// (mirrors the domain transition used across these tests).
/// </summary>
internal static class SaleTestExtensions
{
    public static Sale AlsoApprove(this Sale sale)
    {
        sale.Approve(TestValues.ApprovedByUserId);
        return sale;
    }
}

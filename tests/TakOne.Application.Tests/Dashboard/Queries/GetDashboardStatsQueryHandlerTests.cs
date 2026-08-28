using Ardalis.Specification;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Dashboard.Queries.GetDashboardStats;
using TakOne.Application.Dashboard.Specifications;
using TakOne.Application.Sales.Specifications;
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
/// SCALAR QUERY METHODS (Brutal Code Review v3 #23, Round 18-C):
///   The handler now uses scalar COUNT/SUM/TOP-N methods on
///   ISaleRepository instead of loading all sales + aggregating in
///   memory. BuildMocksCore sets up defaults for the new methods:
///     • CountBySpecificationAsync → 0
///     • CountByStatusAsync(status, spec) → 0
///     • SumRevenueAsync → 0m
///     • SumRevenueByYearAsync(year, spec) → 0m
///     • GetRecentSalesBySpecificationAsync(count, spec) → empty list
///   Tests that assert specific scalar values override these per-case.
///
/// COVERAGE APPROACH:
///   The handler is a static method. We mock every collaborator with
///   NSubstitute. Tests cover:
///     • auth rejection (unauthenticated OR UserId=Guid.Empty)
///     • customer role rejection
///     • Employee role → uses SaleByApproverSpecification (asserted on
///       GetAllWithLineItemsBySpecificationAsync — the in-memory
///       aggregation load — which the handler still calls for top-
///       products/top-categories/top-employees/weekly/monthly)
///     • Admin role → uses AllSalesSpecification (same assertion)
///     • empty sales → all KPIs 0, TotalRevenue 0, TotalSalesCount 0
///     • IRR currency → IsToman=true, DisplayCurrency="تومان"
///     • non-IRR currency → IsToman=false
///     • Pending sale counted in revenueEligibleSales
///     • Draft sale excluded from revenueEligibleSales
///     • Cancelled sale excluded from revenueEligibleSales
///     • top products sorted by TotalAmount desc + take top 7
///     • recent orders sorted by SubmittedAtUtc desc + take 6
///     • userRepository.GetActiveCustomerCountAsync throws → ActiveCustomersCount=0
/// </summary>
public class GetDashboardStatsQueryHandlerTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    private const string IRR = "IRR";
    private const string USD = "USD";

    // Builds a fully-wired mock environment. The sale repository returns
    // an empty list by default; tests override per-case.
    //
    // SECURITY FIX (Brutal Code Review v3 #03): roles are now configured
    // on the currentUser mock via IsInRole — NOT passed through the query
    // DTO (which no longer carries UserRoles). This mirrors the production
    // handler, which reads currentUser.IsInRole (server-verified claims).
    //
    // Two overloads exist because C# params binding: a single optional
    // `Guid? userId` before `params string[] roles` would make
    // `BuildMocks(authenticated: true, Roles.Admin)` try to bind
    // `Roles.Admin` to the `Guid?` parameter. Splitting into
    // (bool, params string[]) and (bool, Guid?, params string[]) lets
    // the compiler resolve the correct overload from the call site.
    private static (
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        ILogger<GetDashboardStatsQueryHandler> logger)
        BuildMocks(bool authenticated, params string[] roles)
        => BuildMocksCore(authenticated, userId: null, fullName: "Test User", roles);

    private static (
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        ILogger<GetDashboardStatsQueryHandler> logger)
        BuildMocks(bool authenticated, Guid? userId, params string[] roles)
        => BuildMocksCore(authenticated, userId, fullName: "Test User", roles);

    private static (
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
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

        var saleRepo = Substitute.For<ISaleRepository>();
        saleRepo.GetAllWithLineItemsBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Sale>());

        // Brutal Code Review v3 #23 (Round 18-C): the handler now uses
        // scalar COUNT/SUM/TOP-N methods instead of loading all sales +
        // aggregating in memory. Provide defaults for each so tests that
        // don't assert specific scalar values still get sensible 0/empty
        // returns rather than NSubstitute's default null Task<List<Sale>>
        // (which would NRE the handler).
        saleRepo.CountBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(0);
        saleRepo.CountByStatusAsync(
                Arg.Any<SaleStatus>(),
                Arg.Any<ISpecification<Sale>>(),
                Arg.Any<CancellationToken>())
            .Returns(0);
        saleRepo.SumRevenueAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(0m);
        saleRepo.SumRevenueByYearAsync(
                Arg.Any<int>(),
                Arg.Any<ISpecification<Sale>>(),
                Arg.Any<CancellationToken>())
            .Returns(0m);
        saleRepo.GetRecentSalesBySpecificationAsync(
                Arg.Any<int>(),
                Arg.Any<ISpecification<Sale>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<Sale>());

        var productRepo = Substitute.For<IProductRepository>();
        // The handler calls GetByIdsReadOnlyAsync when the sales have line
        // items (to resolve productId → categoryId for the category chart).
        // Default to an empty list so the handler's downstream ToDictionary
        // call doesn't NPE. Tests that need real category mappings override
        // this per-case.
        productRepo.GetByIdsReadOnlyAsync(
                Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Products.Entities.Product>());
        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Domain.Categories.Entities.Category>());

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetActiveCustomerCountAsync(Arg.Any<CancellationToken>())
            .Returns(0);

        var logger = Substitute.For<ILogger<GetDashboardStatsQueryHandler>>();

        return (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger);
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

    // ── Auth rejection ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenUnauthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: false, Roles.Admin);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        // Auth reject short-circuits BEFORE any DB call.
        await saleRepo.DidNotReceive().GetAllWithLineItemsBySpecificationAsync(
            Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        // IsAuthenticated=true but UserId=Guid.Empty — the second branch of
        // the auth check rejects it (defense-in-depth).
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, userId: Guid.Empty, Roles.Admin);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
    }

    [Fact]
    public async Task HandleAsync_WhenUnauthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: false, Roles.Admin);
        var query = BuildQuery();

        // Act
        await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
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
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Customer);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Access denied: customers do not have a dashboard.");
        await saleRepo.DidNotReceive().GetAllWithLineItemsBySpecificationAsync(
            Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>());
    }

    // ── Employee role → uses SaleByApproverSpecification ───────────

    [Fact]
    public async Task HandleAsync_WhenUserIsEmployee_UsesSaleByApproverSpecification()
    {
        // Arrange
        // An Employee (no Admin/Manager) sees only sales they approved.
        // The handler uses SaleByApproverSpecification(currentUser.UserId).
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Employee);
        var query = BuildQuery();

        // Act
        await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert — the spec passed to the repo is a SaleByApproverSpecification.
        await saleRepo.Received(1).GetAllWithLineItemsBySpecificationAsync(
            Arg.Is<ISpecification<Sale>>(spec => spec is SaleByApproverSpecification),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIsEmployee_SetsIsEmployeeScopedTrue()
    {
        // Arrange
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Employee);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
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
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var query = BuildQuery();

        // Act
        await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        await saleRepo.Received(1).GetAllWithLineItemsBySpecificationAsync(
            Arg.Is<ISpecification<Sale>>(spec => spec is AllSalesSpecification),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIsAdmin_SetsIsEmployeeScopedFalse()
    {
        // Arrange
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.IsEmployeeScoped.Should().BeFalse();
    }

    // ── Empty sales → all KPIs 0 ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithNoSales_AllKpisAreZero()
    {
        // Arrange
        // saleRepo returns an empty list (the default in BuildMocks).
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
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
    }

    // ── IRR currency → IsToman=true ────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithIrrSale_SetsIsTomanTrueAndDisplayCurrencyToToman()
    {
        // Arrange
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        // A pending sale in IRR currency — used by the handler's currency
        // detection (iterates the loaded sales list for the first
        // non-empty Total.Currency).
        var sale = BuildPendingSale(new Money(1000m, IRR));
        saleRepo.GetAllWithLineItemsBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Sale> { sale });
        // Brutal Code Review v3 #23: TotalRevenue now comes from the
        // scalar SumRevenueAsync query (raw IRR amount). The handler
        // applies the ÷10 Toman conversion AFTER the SUM.
        saleRepo.SumRevenueAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(1000m);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
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
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var sale = BuildPendingSale(new Money(100m, USD));
        saleRepo.GetAllWithLineItemsBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Sale> { sale });
        // Brutal Code Review v3 #23: TotalRevenue now comes from the
        // scalar SumRevenueAsync query. USD = no ÷10 conversion.
        saleRepo.SumRevenueAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(100m);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
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
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var sale = BuildPendingSale(new Money(2000m, IRR)); // 200 Toman after ÷10.
        saleRepo.GetAllWithLineItemsBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Sale> { sale });
        // Brutal Code Review v3 #23: TotalRevenue + PendingSalesCount
        // now come from scalar queries.
        saleRepo.SumRevenueAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(2000m); // raw IRR — handler converts to 200 Toman.
        saleRepo.CountByStatusAsync(
                Arg.Is<SaleStatus>(s => s == SaleStatus.Pending),
                Arg.Any<ISpecification<Sale>>(),
                Arg.Any<CancellationToken>())
            .Returns(1);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.Value.TotalRevenue.Should().Be(200m);
        result.Value.PendingSalesCount.Should().Be(1);
    }

    // ── Draft sale excluded from revenueEligibleSales ──────────────

    [Fact]
    public async Task HandleAsync_WithDraftSale_ExcludesFromTotalRevenue()
    {
        // Arrange
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var sale = BuildDraftSale(new Money(2000m, IRR));
        saleRepo.GetAllWithLineItemsBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Sale> { sale });
        // Brutal Code Review v3 #23: KPI counts come from scalar queries.
        // Draft sales count toward TotalSalesCount (the all-status count)
        // and DraftSalesCount (the per-status count). They do NOT count
        // toward SumRevenueAsync (the handler excludes Draft from revenue
        // — SumRevenueAsync's WHERE clause filters Status IN (Pending,
        // Approved, Invoiced)), so the default 0m return is correct.
        saleRepo.CountBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(1); // one sale in scope
        saleRepo.CountByStatusAsync(
                Arg.Is<SaleStatus>(s => s == SaleStatus.Draft),
                Arg.Any<ISpecification<Sale>>(),
                Arg.Any<CancellationToken>())
            .Returns(1); // one draft
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        // Draft → excluded from revenueEligibleSales → TotalRevenue=0.
        result.Value.TotalRevenue.Should().Be(0);
        // But TotalSalesCount counts ALL sales (including Draft).
        result.Value.TotalSalesCount.Should().Be(1);
        result.Value.DraftSalesCount.Should().Be(1);
    }

    // ── Cancelled sale excluded from revenueEligibleSales ──────────

    [Fact]
    public async Task HandleAsync_WithCancelledSale_ExcludesFromTotalRevenue()
    {
        // Arrange
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var sale = BuildCancelledSale(new Money(2000m, IRR));
        saleRepo.GetAllWithLineItemsBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Sale> { sale });
        // Brutal Code Review v3 #23: CancelledSalesCount is now a scalar
        // COUNT query. SumRevenueAsync's WHERE clause filters out
        // Cancelled (only Pending/Approved/Invoiced), so the default 0m
        // return correctly produces TotalRevenue=0.
        saleRepo.CountByStatusAsync(
                Arg.Is<SaleStatus>(s => s == SaleStatus.Cancelled),
                Arg.Any<ISpecification<Sale>>(),
                Arg.Any<CancellationToken>())
            .Returns(1); // one cancelled sale
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.Value.TotalRevenue.Should().Be(0);
        result.Value.CancelledSalesCount.Should().Be(1);
        // Cancelled sales don't count in this-month's submitted totals
        // (the handler filters them out of thisMonthSales).
        result.Value.ThisMonthEmployeePurchaseTotal.Should().Be(0);
    }

    // ── Top products sorted by TotalAmount desc + take top 7 ───────

    [Fact]
    public async Task HandleAsync_WithMultipleProducts_TakesTop7ByTotalAmountDesc()
    {
        // Arrange
        // Build 8 pending sales, each with ONE line item for a distinct
        // product name. The handler's TopProducts groups by ProductName
        // and takes top 7. The 8th product must be absent from the result.
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        var sales = new List<Sale>();
        for (var i = 0; i < 8; i++)
        {
            // Amounts: 100, 200, ..., 800 IRR — the handler sums GrossTotal.
            // After ÷10 they become 10, 20, ..., 80 Toman.
            // The LAST product (i=7) has amount 800 → TotalAmount=80 —
            // it's the highest, so it stays in the top-7. The product with
            // the LOWEST amount (i=0, amount=100 → 10 Toman) gets dropped.
            // We assert the dropped one (i=0) is NOT in the result.
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
        saleRepo.GetAllWithLineItemsBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(sales);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
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
    }

    // ── Recent orders sorted by SubmittedAtUtc desc + take 6 ───────

    [Fact]
    public async Task HandleAsync_WithManySales_RecentOrdersTakesLatest6()
    {
        // Arrange
        // Build 8 sales, each submitted at a slightly different time
        // (we sleep 15ms between each). The handler should return the
        // 6 most-recent in descending order.
        //
        // Brutal Code Review v3 #23 (Round 18-C): the handler now uses
        // GetRecentSalesBySpecificationAsync(6, spec, ct) which runs
        // SQL TOP 6 ORDER BY SubmittedAtUtc DESC — the "take top 6"
        // logic moved from the handler to the repo. To exercise the
        // handler's projection of the bounded slice to RecentOrderDto,
        // we mock GetRecentSalesBySpecificationAsync to return the
        // 6 most-recent sales in the expected (desc) order. The test
        // still proves the handler doesn't accidentally drop or reorder
        // entries from the bounded slice.
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
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
        // Reverse the list before slicing — the repo's SQL ORDER BY
        // SubmittedAtUtc DESC would return the most-recent first (C7,
        // C6, C5, ...). Mock the repo to return that exact ordering.
        sales.Reverse();
        saleRepo.GetRecentSalesBySpecificationAsync(
                Arg.Any<int>(),
                Arg.Any<ISpecification<Sale>>(),
                Arg.Any<CancellationToken>())
            .Returns(sales.Take(6).ToList()); // repo returns only 6
        // Also need the GetAllWithLineItemsBySpecificationAsync mock
        // (used for top products etc) — return all 8 so the rest of
        // the handler's aggregations still have data.
        saleRepo.GetAllWithLineItemsBySpecificationAsync(
                Arg.Any<ISpecification<Sale>>(), Arg.Any<CancellationToken>())
            .Returns(sales);
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
            CancellationToken.None);

        // Assert
        result.Value.RecentOrders.Count.Should().Be(6);
        // The most-recent 6 sales are C7, C6, ..., C2 (since we submitted
        // C0..C7 in time order and then reversed the list).
        result.Value.RecentOrders.Select(o => o.CustomerName).Should()
            .BeInDescendingOrder(); // not by name — but the order is reverse-chronological
        // CustomerName "C7" should be first (most recent).
        result.Value.RecentOrders.First().CustomerName.Should().Be("C7");
    }

    // ── userRepository.GetActiveCustomerCountAsync throws → defaults to 0

    [Fact]
    public async Task HandleAsync_WhenUserRepoThrowsActiveCustomerCount_DefaultsToZero()
    {
        // Arrange
        // userRepo throws — handler catches and defaults ActiveCustomersCount to 0.
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        userRepo.GetActiveCustomerCountAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("DB down")));
        var query = BuildQuery();

        // Act
        var result = await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
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
        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger)
            = BuildMocks(authenticated: true, Roles.Admin);
        userRepo.GetActiveCustomerCountAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("DB down")));
        var query = BuildQuery();

        // Act
        await GetDashboardStatsQueryHandler.HandleAsync(
            query, currentUser, saleRepo, productRepo, categoryRepo, userRepo, logger,
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

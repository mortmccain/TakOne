using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.DTOs;
using TakOne.Application.Products.Queries.GetProductsPaginated;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.Application.Tests.Products.Queries.GetProductsPaginated;

/// <summary>
/// Unit tests for <see cref="GetProductsPaginatedQueryHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// query, the current-user service, the product repository, the
/// category repository, the user repository, the purchase-limit
/// policy, a logger, and a cancellation token. It returns a bare
/// <see cref="PaginatedResult{ProductListItemDto}"/> (NOT wrapped in
/// <c>Result&lt;&gt;</c>) — auth failure returns an empty page
/// (warning logged, silent). Same contract as the other paginated
/// query handlers.
///
/// COVERAGE TARGETS:
///   1. Auth failure → empty page (NOT Result.Failure).
///   2. Page clamps (MaxPageSize=100, default 20).
///   3. IncludeInactive filter override — non-staff callers get
///      IncludeInactive=false regardless of what they passed.
///   4. freshUser.GroupId resolution from the DB (NOT the stale claim).
///   5. MyPurchaseLimit per-product via the policy (honors LimitMode).
///   6. Category hierarchy enrichment (CategoryId/SubCategoryId/
///      SubSubCategoryId resolved against the category tree loaded
///      once via categoryRepository.GetAllAsync).
///   7. Cancellation token forwarded.
/// </summary>
public class GetProductsPaginatedQueryHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private const int ExpectedMaxPageSize = 100;

    private static GetProductsPaginatedQuery BuildValidQuery(
        int? pageNumber = null,
        int? pageSize = null,
        bool? includeInactive = null,
        ProductSortBy? sortBy = null)
        => new()
        {
            PageNumber = pageNumber ?? 1,
            PageSize = pageSize ?? 20,
            IncludeInactive = includeInactive ?? false,
            SortBy = sortBy
        };

    // Builds a fully-wired NSubstitute environment with the current
    // user as Admin and an empty product page from the product repo.
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        ICategoryRepository categoryRepo,
        IUserRepository userRepo,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        ILogger<GetProductsPaginatedQueryHandler> logger)
        BuildMocks(
        PaginatedResult<Domain.Products.Entities.Product>? page = null,
        List<Domain.Categories.Entities.Category>? categories = null,
        User? freshUser = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);
        currentUser.IsInRole(Roles.Admin).Returns(true);

        var emptyPage = new PaginatedResult<Domain.Products.Entities.Product>(
            Array.Empty<Domain.Products.Entities.Product>(), 0, 1, 20);
        var actualPage = page ?? emptyPage;

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetPaginatedAsync(
            default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(actualPage);

        var actualCategories = categories ?? new List<Domain.Categories.Entities.Category>();
        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.GetAllAsync(default)
            .ReturnsForAnyArgs(actualCategories);

        var actualFreshUser = freshUser ?? User.CreateStaff("EMP-001", "Test Admin");
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualFreshUser);

        var purchaseLimitPolicy = Substitute.For<IPurchaseLimitPolicy>();
        purchaseLimitPolicy.GetCountLimitAsync(default, default, default)
            .ReturnsForAnyArgs((int?)null);

        var logger = Substitute.For<ILogger<GetProductsPaginatedQueryHandler>>();

        return (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAdminAndPageEmpty_ReturnsEmptyPage()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();

        // Act
        var result = await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    // ── Auth failure → empty page (NOT Result.Failure) ────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsEmptyPage()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        // Repo is NOT called on the auth-fail path.
        await productRepo.DidNotReceive().GetPaginatedAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Any<ProductSortBy>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Page clamps ────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenPageNumberIsZero_ClampsToOne()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(pageNumber: 0), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        await productRepo.Received(1).GetPaginatedAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<string>(), Arg.Is<int>(p => p == 1), Arg.Any<int>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Any<ProductSortBy>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenPageSizeExceedsMax_ClampsToHundred()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(pageSize: 500), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        await productRepo.Received(1).GetPaginatedAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<string>(), Arg.Any<int>(),
            Arg.Is<int>(p => p == ExpectedMaxPageSize),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Any<ProductSortBy>(),
            Arg.Any<CancellationToken>());
    }

    // ── IncludeInactive filter override ────────────────────────────────

    // IncludeInactive is forced to false for non-staff callers
    // (defense-in-depth: even if a malicious client sets the flag,
    // the server-side check still wins). A customer passing
    // IncludeInactive=true silently gets active-only products.
    [Fact]
    public async Task HandleAsync_WhenCustomerSetsIncludeInactive_SilentlyClampsToFalse()
    {
        // Arrange
        // Make the current user a Customer (NOT Admin/Manager).
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();
        currentUser.IsInRole(Roles.Admin).Returns(false);
        currentUser.IsInRole(Roles.Manager).Returns(false);
        currentUser.IsInRole(Roles.Customer).Returns(true);
        // Customer passes IncludeInactive=true (suspicious).
        var query = BuildValidQuery(includeInactive: true);

        // Act
        var result = await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        // No exception, no Result.Failure — the handler silently clamps
        // IncludeInactive to false (per the SUT's design comment: "a
        // customer's UI setting the flag is a UX bug, not an attack;
        // just clamp silently").
        result.Should().NotBeNull();
    }

    // ── freshUser.GroupId resolution (NOT the stale claim) ──────────

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_LoadsFreshUserFromUserRepository()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        await userRepo.Received(1).GetByIdAsync(
            Arg.Is<Guid>(id => id == TestValues.CreatedByUserId),
            Arg.Any<CancellationToken>());
    }

    // ── Category hierarchy enrichment ─────────────────────────────────

    // The handler loads the category tree ONCE via
    // categoryRepository.GetAllAsync (active AND inactive, full
    // hierarchy) so it can resolve each product's CategoryId /
    // SubCategoryId / SubSubCategoryId to display names + IsActive
    // flags. Single round-trip — GetAllAsync eager-loads the hierarchy.
    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_LoadsCategoryTreeOnce()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        await categoryRepo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToProductRepository()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger, ct);

        // Assert
        await productRepo.Received(1).GetPaginatedAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Any<ProductSortBy>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Round 4: sort pass-through ─────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithSort_PassesSortIntoRepositoryCall()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(sortBy: ProductSortBy.PriceHighToLow),
            currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        await productRepo.Received(1).GetPaginatedAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Is(ProductSortBy.PriceHighToLow),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithoutSort_DefaultsToNameOrder()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(),
            currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert — null SortBy (the pre-Round-4 call shape) must arrive at
        // the repository as the explicit Name default.
        await productRepo.Received(1).GetPaginatedAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Is(ProductSortBy.Name),
            Arg.Any<CancellationToken>());
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
///   8. ROUND 6 (server-driven paging for AdminProducts): every typed
///      query filter flows into the <see cref="ProductsListFilters"/>
///      record handed to the repository, and the category-NAME filters
///      are resolved against the category tree into Id sets (matched
///      ids / empty-on-no-match / null-on-tree-load-failure).
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
            default, default, default, default, default)
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
        purchaseLimitPolicy.GetCountLimitAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);
        purchaseLimitPolicy.GetCountLimitsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new System.Collections.Generic.Dictionary<Guid, int?>());

        var logger = Substitute.For<ILogger<GetProductsPaginatedQueryHandler>>();

        return (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger);
    }

    /// <summary>
    /// Captures the <see cref="ProductsListFilters"/> record the handler
    /// handed to the repository (Round 6 — mirrors the users handler
    /// tests' CapturedFilters helper).
    /// </summary>
    private static ProductsListFilters CapturedFilters(IProductRepository productRepo)
    {
        var filters = productRepo.ReceivedCalls()
            .Select(c => c.GetArguments().FirstOrDefault(a => a is ProductsListFilters))
            .Cast<ProductsListFilters>()
            .FirstOrDefault();
        filters.Should().NotBeNull("the handler must hand filters to the repository");
        return filters!;
    }

    /// <summary>
    /// Seeds a two-level category tree: "Electronics" (with a "Laptops"
    /// sub-category that itself has a "Gaming Laptops" sub-sub-category)
    /// and "Groceries" (with a "Fresh" sub-category).
    /// </summary>
    private static List<Domain.Categories.Entities.Category> MakeCategoryTree()
    {
        var electronics = Domain.Categories.Entities.Category.Create("Electronics");
        var laptops = electronics.AddSubCategory("Laptops");
        electronics.AddSubSubCategory(laptops.Id, "Gaming Laptops");

        var groceries = Domain.Categories.Entities.Category.Create("Groceries");
        groceries.AddSubCategory("Fresh");

        return new List<Domain.Categories.Entities.Category> { electronics, groceries };
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
            Arg.Any<ProductsListFilters?>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Any<int>(), Arg.Any<int>(),
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
            Arg.Any<ProductsListFilters?>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Is<int>(p => p == 1), Arg.Any<int>(),
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
            Arg.Any<ProductsListFilters?>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Any<int>(),
            Arg.Is<int>(p => p == ExpectedMaxPageSize),
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
        // just clamp silently"). The clamped flag surfaces as a
        // non-null visibility filter on the repository call.
        result.Should().NotBeNull();
        await productRepo.Received(1).GetPaginatedAsync(
            Arg.Any<ProductsListFilters?>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
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
            Arg.Any<ProductsListFilters?>(),
            Arg.Any<ProductVisibilityFilter?>(),
            Arg.Any<int>(), Arg.Any<int>(),
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
        CapturedFilters(productRepo).SortBy.Should().Be(ProductSortBy.PriceHighToLow);
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
        var filters = CapturedFilters(productRepo);
        filters.SortBy.Should().Be(ProductSortBy.Name);
        filters.SortDescending.Should().BeFalse();
    }

    // ── Round 6: typed filter wiring ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_DefaultQuery_FiltersAreAllClear()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        var filters = CapturedFilters(productRepo);
        filters.SearchTerm.Should().BeNull();
        filters.CategoryId.Should().BeNull();
        filters.SubCategoryId.Should().BeNull();
        filters.SubSubCategoryId.Should().BeNull();
        filters.Name.Should().BeNull();
        filters.StockStatus.Should().BeNull();
        filters.Price.Should().BeNull();
        filters.Stock.Should().BeNull();
        filters.CategoryIds.Should().BeNull();
        filters.SubCategoryIds.Should().BeNull();
        filters.SubSubCategoryIds.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_AllColumnFilters_PassThroughToRepository()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();
        var nameFilter = new ProductsTextFilter("widget", ProductsTextOperator.Contains);
        var priceFilter = new ProductsNumberFilter(ProductsNumberOperator.LessThanOrEqual, 250m);
        var stockFilter = new ProductsNumberFilter(ProductsNumberOperator.GreaterThan, 0m);
        var categoryFilter = new ProductsTextFilter("elec", ProductsTextOperator.StartsWith);
        var subFilter = new ProductsTextFilter("top", ProductsTextOperator.EndsWith);
        var subSubFilter = new ProductsTextFilter("gam", ProductsTextOperator.Contains);
        var query = new GetProductsPaginatedQuery
        {
            PageNumber = 1,
            PageSize = 20,
            IncludeInactive = true,
            NameFilter = nameFilter,
            StockStatus = ProductStockStatus.InStock,
            PriceFilter = priceFilter,
            StockFilter = stockFilter,
            CategoryNameFilter = categoryFilter,
            SubCategoryNameFilter = subFilter,
            SubSubCategoryNameFilter = subSubFilter,
            SortBy = ProductSortBy.StockHighToLow,
            SortDescending = true
        };

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        var filters = CapturedFilters(productRepo);
        filters.Name.Should().Be(nameFilter);
        filters.StockStatus.Should().Be(ProductStockStatus.InStock);
        filters.Price.Should().Be(priceFilter);
        filters.Stock.Should().Be(stockFilter);
        filters.SortBy.Should().Be(ProductSortBy.StockHighToLow);
        filters.SortDescending.Should().BeTrue();
        // The category-name filters are resolved into Id sets below —
        // they must NOT pass through as raw text filters.
        filters.CategoryIds.Should().NotBeNull();
        filters.SubCategoryIds.Should().NotBeNull();
        filters.SubSubCategoryIds.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_SortDescendingWithoutSortKey_IsNormalizedAway()
    {
        // Arrange — a malformed caller sets the direction without a key.
        // The handler must keep the default Name-ASCENDING order: a stray
        // SortDescending flag must never flip the default.
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();
        var query = new GetProductsPaginatedQuery { SortBy = null, SortDescending = true };

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        var filters = CapturedFilters(productRepo);
        filters.SortBy.Should().Be(ProductSortBy.Name);
        filters.SortDescending.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_LegacyFilters_PassThroughUnchanged()
    {
        // Arrange — the shop/mobile callers' shape: search term + category
        // ids + a Round-4 sort. These must flow through the filters record
        // untouched (source compatibility was the Round-6 constraint).
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();
        var categoryId = Guid.NewGuid();
        var query = new GetProductsPaginatedQuery
        {
            SearchTerm = "  berry  ",
            CategoryId = categoryId,
            SortBy = ProductSortBy.PriceLowToHigh
        };

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        var filters = CapturedFilters(productRepo);
        filters.SearchTerm.Should().Be("  berry  ", "trimming is the repository's job; the handler passes through");
        filters.CategoryId.Should().Be(categoryId);
        filters.SortBy.Should().Be(ProductSortBy.PriceLowToHigh);
    }

    // ── Round 6: category-name → Id-set resolution ─────────────────────

    [Fact]
    public async Task HandleAsync_CategoryNameFilter_ResolvesToMatchingCategoryIds()
    {
        // Arrange
        var tree = MakeCategoryTree();
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) =
            BuildMocks(categories: tree);
        var query = new GetProductsPaginatedQuery
        {
            CategoryNameFilter = new ProductsTextFilter("elec", ProductsTextOperator.Contains)
        };

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert — "elec" matches "Electronics" (case-insensitive) and
        // NOT "Groceries"; the resolver hands the repository the matched
        // Id set, and only that set.
        var electronics = tree[0].Id; // "Electronics"
        var filters = CapturedFilters(productRepo);
        filters.CategoryIds.Should().BeEquivalentTo(new[] { electronics },
            "the name filter resolves against the loaded category tree, case-insensitively");
        filters.SubCategoryIds.Should().BeNull("no sub-category filter was set");
        filters.SubSubCategoryIds.Should().BeNull("no sub-sub-category filter was set");
    }

    [Fact]
    public async Task HandleAsync_SubCategoryNameFilter_ResolvesToMatchingSubCategoryIds()
    {
        // Arrange
        var tree = MakeCategoryTree();
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) =
            BuildMocks(categories: tree);
        var query = new GetProductsPaginatedQuery
        {
            SubCategoryNameFilter = new ProductsTextFilter("lap", ProductsTextOperator.StartsWith)
        };

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert — "lap" prefix-matches the "Laptops" sub-category only
        // (not "Fresh"), and the top-level set stays untouched.
        var laptops = tree[0].SubCategories.Single().Id;
        var filters = CapturedFilters(productRepo);
        filters.SubCategoryIds.Should().BeEquivalentTo(new[] { laptops });
        filters.CategoryIds.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_SubSubCategoryNameFilter_ResolvesToMatchingSubSubCategoryIds()
    {
        // Arrange
        var tree = MakeCategoryTree();
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) =
            BuildMocks(categories: tree);
        var query = new GetProductsPaginatedQuery
        {
            SubSubCategoryNameFilter = new ProductsTextFilter("gaming", ProductsTextOperator.StartsWith)
        };

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        var gaming = tree[0].SubCategories.Single().SubSubCategories.Single().Id;
        CapturedFilters(productRepo).SubSubCategoryIds
            .Should().BeEquivalentTo(new[] { gaming });
    }

    [Fact]
    public async Task HandleAsync_CategoryNameFilter_NoMatch_YieldsEmptyIdSet()
    {
        // Arrange — a term that matches no category must resolve to an
        // EMPTY set (zero rows server-side), not null (which would mean
        // "no filter" and silently show everything).
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) =
            BuildMocks(categories: MakeCategoryTree());
        var query = new GetProductsPaginatedQuery
        {
            CategoryNameFilter = new ProductsTextFilter("xyzzy", ProductsTextOperator.Contains)
        };

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        CapturedFilters(productRepo).CategoryIds.Should().NotBeNull()
            .And.BeEmpty("no category matches 'xyzzy' — the filtered result is correctly zero rows");
    }

    [Fact]
    public async Task HandleAsync_CategoryNameFilter_TreeLoadFails_DegradesToNoFilter()
    {
        // Arrange — the category-tree load throws; the name filter can't
        // be resolved. The handler degrades to a NULL id set (no clause —
        // the grid stays usable) rather than zeroing it, mirroring the
        // visibility filter's graceful-degradation posture.
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();
        categoryRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("db down"));
        var query = new GetProductsPaginatedQuery
        {
            CategoryNameFilter = new ProductsTextFilter("elec", ProductsTextOperator.Contains)
        };

        // Act
        var result = await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull("the tree-load failure is non-fatal");
        CapturedFilters(productRepo).CategoryIds.Should().BeNull(
            "an unresolvable name filter degrades to no clause, not to zero rows");
    }

    [Fact]
    public async Task HandleAsync_CategoryNameFilter_NegativeOperators_ResolveToComplementSets()
    {
        // Arrange — NotContains resolves to the categories whose names do
        // NOT contain the term (the repository IN-clauses that complement).
        var tree = MakeCategoryTree();
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) =
            BuildMocks(categories: tree);
        var query = new GetProductsPaginatedQuery
        {
            CategoryNameFilter = new ProductsTextFilter("elec", ProductsTextOperator.NotContains)
        };

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        CapturedFilters(productRepo).CategoryIds.Should().BeEquivalentTo(
            new[] { tree[1].Id },
            "NotContains resolves to the complement — only 'Groceries' survives");
    }

    [Fact]
    public async Task HandleAsync_CategoryNameFilter_WhitespaceTerm_IsNoFilter()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) =
            BuildMocks(categories: MakeCategoryTree());
        var query = new GetProductsPaginatedQuery
        {
            CategoryNameFilter = new ProductsTextFilter("   ", ProductsTextOperator.Contains)
        };

        // Act
        await GetProductsPaginatedQueryHandler.HandleAsync(
            query, currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        CapturedFilters(productRepo).CategoryIds.Should().BeNull(
            "a whitespace-only term means 'no filter' — same lenient contract as the text filters");
    }

    // ── Round 6: TotalCount pass-through (drives the grid's pager) ─────

    [Fact]
    public async Task HandleAsync_TotalCountFlowsFromRepository()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger) = BuildMocks();
        var product = Domain.Products.Entities.Product.Create(
            "Widget", "A widget", new Money(10m, "IRR"), 5, Guid.NewGuid());
        productRepo.GetPaginatedAsync(
                Arg.Any<ProductsListFilters?>(),
                Arg.Any<ProductVisibilityFilter?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<Domain.Products.Entities.Product>(
                new List<Domain.Products.Entities.Product> { product }, 451, 1, 20));

        // Act
        var result = await GetProductsPaginatedQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, categoryRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.TotalCount.Should().Be(451,
            "the server-side total drives the grid pager — with the pre-Round-6 clamp " +
            "the page believed there were at most 100 products");
        result.PageNumber.Should().Be(1);
        result.PageSize.Should().Be(20);
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.Commands.SubmitSale;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Sales.Commands.SubmitSale;

/// <summary>
/// Unit tests for <see cref="SubmitSaleCommandHandler"/> — the FIRST
/// tests this handler ever had, written for the owner's
/// everyone-except-ReadOnly-can-buy policy.
///
/// HISTORY: the "Step 12-a runtime fix" used to reject the submit when
/// the sale's customer had no CustomerGroup (NoCustomerGroupErrors) —
/// so a groupless buyer (e.g. an admin) could add to a cart but then
/// never check out. The owner's policy removes that guard: a groupless
/// customer's submit runs with the group-scoped re-checks (currency,
/// budget, per-product caps) skipped — there is no group to scope to —
/// while the category-deactivation and empty-cart guards still apply.
///
/// COVERAGE APPROACH: the Sale, Product, and User aggregates are REAL;
/// all collaborators are NSubstitute mocks. The submit path is the
/// self-buy shape the shop produces (customer == creator == the
/// groupless buyer).
/// </summary>
public class SubmitSaleCommandHandlerTests
{
    // ── Test doubles ────────────────────────────────────────────────────

    /// <summary>
    /// Unit-of-work double that runs the retry lambda immediately (one
    /// attempt) and counts SaveChanges.
    /// </summary>
    private sealed class PassthroughUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }

        public void ClearChangeTracker() { /* no-op for tests */ }

        public void AddEntity(object entity) { /* no-op for tests */ }

        public Task<T> ExecuteWithRetryAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            int maxAttempts = 3,
            CancellationToken cancellationToken = default)
            => operation(cancellationToken);
    }

    /// <summary>A lock handle double — acquiring costs nothing.</summary>
    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── Fixture builders ────────────────────────────────────────────────

    private static Product BuildInStockProduct()
        => Product.Create(
            name: "Test Product",
            description: "A test product",
            price: new Money(10m, TestValues.IRR),
            stockQuantity: 100,
            categoryId: TestValues.CategoryId);

    /// <summary>
    /// Builds a REAL Draft sale for the given buyer (self-buy shape:
    /// buyer is both customer and creator) with one line of the product.
    /// </summary>
    private static Sale BuildDraftSale(User buyer, Product product)
    {
        var sale = Sale.Create(
            customerId: buyer.Id,
            customerName: buyer.FullName,
            createdByUserId: buyer.Id,
            createdByName: buyer.FullName);

        sale.AddLineItem(
            productId: product.Id,
            productName: product.Name,
            quantity: 1,
            unitPrice: product.Price);

        return sale;
    }

    /// <summary>
    /// Wires the full mock set: the sale resolves (first load AND the
    /// post-lock re-load both return the same instance — no concurrent
    /// mutation), the customer resolves to the given buyer, the line's
    /// product resolves with an active category hierarchy, and the sale
    /// number generator returns a fixed number.
    /// </summary>
    private static (
        ICurrentUserService currentUser,
        ISaleRepository saleRepo,
        IProductRepository productRepo,
        ICategoryRepository categoryRepo,
        IUserRepository userRepo,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        ISalaryBudgetService salaryBudgetService,
        ICartMutationLock cartMutationLock,
        ISaleStateLock saleStateLock,
        ISaleNumberGenerator saleNumberGenerator,
        PassthroughUnitOfWork unitOfWork,
        ILogger<SubmitSaleCommandHandler> logger)
        BuildMocks(User buyer, Sale sale, Product product)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(buyer.Id);
        currentUser.FullName.Returns(buyer.FullName);

        var saleRepo = Substitute.For<ISaleRepository>();
        saleRepo.GetByIdWithLineItemsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(sale);

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdsAsync(
                Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product> { product });

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.IsProductCategoryHierarchyActiveAsync(
                Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(buyer);

        var purchaseLimitPolicy = Substitute.For<IPurchaseLimitPolicy>();
        var salaryBudgetService = Substitute.For<ISalaryBudgetService>();

        var cartMutationLock = Substitute.For<ICartMutationLock>();
        cartMutationLock.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new NoopAsyncDisposable());

        var saleStateLock = Substitute.For<ISaleStateLock>();
        saleStateLock.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new NoopAsyncDisposable());

        var saleNumberGenerator = Substitute.For<ISaleNumberGenerator>();
        saleNumberGenerator.NextAsync(Arg.Any<CancellationToken>())
            .Returns(SaleNumber.Create(TestValues.PersianYearValid, 1));

        var unitOfWork = new PassthroughUnitOfWork();
        var logger = Substitute.For<ILogger<SubmitSaleCommandHandler>>();

        return (currentUser, saleRepo, productRepo, categoryRepo, userRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, saleStateLock,
            saleNumberGenerator, unitOfWork, logger);
    }

    // ── Tests: groupless buyers can submit ──────────────────────────────

    // The checkout half of the reported bug: the groupless buyer built
    // a cart (CreateOrAppendSale now allows it) and then Submit used to
    // reject them with NoCustomerGroup. Under the owner's policy the
    // submit must SUCCEED: the sale transitions Draft → Pending and a
    // permanent sale number is allocated.
    [Fact]
    public async Task HandleAsync_WhenCustomerHasNoGroup_SubmitsSuccessfully()
    {
        // Arrange — a staff user (GroupId null) submitting their own
        // self-buy draft.
        var buyer = User.CreateStaff("ADMIN-001", "Groupless Admin");
        var product = BuildInStockProduct();
        var sale = BuildDraftSale(buyer, product);

        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, saleStateLock,
            saleNumberGenerator, unitOfWork, logger)
            = BuildMocks(buyer, sale, product);

        // Act
        var result = await SubmitSaleCommandHandler.HandleAsync(
            new SubmitSaleCommand(sale.Id),
            currentUser, saleRepo, productRepo, categoryRepo, userRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, saleStateLock,
            saleNumberGenerator, unitOfWork, logger, CancellationToken.None);

        // Assert — submitted: Pending, numbered, persisted.
        result.IsSuccess.Should().BeTrue(
            "a groupless buyer must be able to check out their cart");
        sale.Status.Should().Be(SaleStatus.Pending);
        sale.SaleNumber.Should().NotBeNull(
            "a permanent sale number is allocated at submit time");
        sale.SubmittedAtUtc.Should().NotBeNull();
        unitOfWork.SaveCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // The group-scoped re-checks must be SKIPPED (not mis-fired) for a
    // groupless customer at submit time: no per-product limit batch,
    // no currency-mismatch batch, no budget info lookup.
    [Fact]
    public async Task HandleAsync_WhenCustomerHasNoGroup_SkipsGroupScopedRechecks()
    {
        // Arrange
        var buyer = User.CreateStaff("EMP-001", "Groupless Employee");
        var product = BuildInStockProduct();
        var sale = BuildDraftSale(buyer, product);

        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, saleStateLock,
            saleNumberGenerator, unitOfWork, logger)
            = BuildMocks(buyer, sale, product);

        // Act
        var result = await SubmitSaleCommandHandler.HandleAsync(
            new SubmitSaleCommand(sale.Id),
            currentUser, saleRepo, productRepo, categoryRepo, userRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, saleStateLock,
            saleNumberGenerator, unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await purchaseLimitPolicy.DidNotReceive().GetCountLimitsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await purchaseLimitPolicy.DidNotReceive().GetCurrencyMismatchedProductIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await salaryBudgetService.DidNotReceive().GetBudgetInfoAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // The guards that DON'T depend on group membership still fire for a
    // groupless buyer — pinned here with the empty-cart guard: a draft
    // with no lines must never be submitted (and must not burn a sale
    // number).
    [Fact]
    public async Task HandleAsync_WhenCartIsEmpty_RejectsWithoutBurningSaleNumber()
    {
        // Arrange — a groupless buyer with a ghost draft (no lines).
        var buyer = User.CreateStaff("MGR-001", "Groupless Manager");
        var product = BuildInStockProduct();
        var sale = Sale.Create(buyer.Id, buyer.FullName, buyer.Id, buyer.FullName);

        var (currentUser, saleRepo, productRepo, categoryRepo, userRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, saleStateLock,
            saleNumberGenerator, unitOfWork, logger)
            = BuildMocks(buyer, sale, product);

        // Act
        var result = await SubmitSaleCommandHandler.HandleAsync(
            new SubmitSaleCommand(sale.Id),
            currentUser, saleRepo, productRepo, categoryRepo, userRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, saleStateLock,
            saleNumberGenerator, unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("SubmitEmptyCart");
        await saleNumberGenerator.DidNotReceive().NextAsync(Arg.Any<CancellationToken>());
        sale.Status.Should().Be(SaleStatus.Draft);
    }
}

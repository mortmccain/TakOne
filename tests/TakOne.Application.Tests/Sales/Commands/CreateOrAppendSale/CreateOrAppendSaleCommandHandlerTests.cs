using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.Commands.CreateOrAppendSale;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Sales.Commands.CreateOrAppendSale;

/// <summary>
/// Unit tests for <see cref="CreateOrAppendSaleCommandHandler"/> — the
/// FIRST tests this handler ever had, written for the owner's
/// everyone-except-ReadOnly-can-buy policy.
///
/// HISTORY: the "Step 12-a runtime fix" used to reject EVERY buyer
/// without a CustomerGroup with <c>NoCustomerGroupErrors</c> — which
/// is exactly why the owner (an admin without a salary group) could
/// not buy anything. The owner's policy replaces that guard: a
/// groupless buyer is served normally, and every group-scoped check
/// (currency match, salary budget, per-product cap) simply doesn't
/// apply because there is no group to scope to. Stock still caps the
/// purchase.
///
/// COVERAGE APPROACH: the Sale and Product aggregates are REAL, all
/// collaborators are NSubstitute mocks. The two paths exercised are
/// the CREATE path (no active draft → fresh Sale via Sale.Create) and
/// the APPEND path (existing draft → AddLineItem). The groupless
/// buyer is built with <see cref="User.CreateStaff"/> (GroupId is
/// always null for staff users).
/// </summary>
public class CreateOrAppendSaleCommandHandlerTests
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
    /// Wires the full mock set for the GROUPLESS-buyer happy path:
    /// the user repository resolves a staff user (GroupId null), the
    /// product is in stock, its category hierarchy is active, and no
    /// active draft exists by default (each test overrides the draft
    /// when it exercises the APPEND path).
    /// </summary>
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        ISaleRepository saleRepo,
        IUserRepository userRepo,
        ICategoryRepository categoryRepo,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        ISalaryBudgetService salaryBudgetService,
        ICartMutationLock cartMutationLock,
        PassthroughUnitOfWork unitOfWork,
        ILogger<CreateOrAppendSaleCommandHandler> logger)
        BuildMocks(User buyer, Product product)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(buyer.Id);
        currentUser.FullName.Returns(buyer.FullName);

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(product);

        var saleRepo = Substitute.For<ISaleRepository>();
        saleRepo.GetActiveDraftForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Sale?)null);

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(buyer);

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.IsProductCategoryHierarchyActiveAsync(
                Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var purchaseLimitPolicy = Substitute.For<IPurchaseLimitPolicy>();
        var salaryBudgetService = Substitute.For<ISalaryBudgetService>();

        var cartMutationLock = Substitute.For<ICartMutationLock>();
        cartMutationLock.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new NoopAsyncDisposable());

        var unitOfWork = new PassthroughUnitOfWork();
        var logger = Substitute.For<ILogger<CreateOrAppendSaleCommandHandler>>();

        return (currentUser, productRepo, saleRepo, userRepo, categoryRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, unitOfWork, logger);
    }

    // ── Tests: groupless buyers can buy ─────────────────────────────────

    // THE reported bug: an admin (never assigned to a salary group)
    // clicks "add to cart" and was rejected with NoCustomerGroup.
    // Owner policy: every authenticated user except pure-ReadOnly can
    // buy — so this must now CREATE the draft and succeed.
    [Fact]
    public async Task HandleAsync_WhenBuyerHasNoGroup_CreatesDraftWithLine()
    {
        // Arrange — a staff user: CreateStaff ALWAYS has GroupId null.
        var buyer = User.CreateStaff("ADMIN-001", "Groupless Admin");
        var product = BuildInStockProduct();
        var (currentUser, productRepo, saleRepo, userRepo, categoryRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, unitOfWork, logger)
            = BuildMocks(buyer, product);

        Sale? registered = null;
        // Arrangement (not a real invocation) — discard to satisfy
        // CS4014 under TreatWarningsAsErrors.
        _ = saleRepo.AddAsync(Arg.Do<Sale>(s => registered = s), Arg.Any<CancellationToken>());

        var command = new CreateOrAppendSaleCommand(product.Id, 2);

        // Act
        var result = await CreateOrAppendSaleCommandHandler.HandleAsync(
            command, currentUser, productRepo, saleRepo, userRepo, categoryRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock,
            unitOfWork, logger, CancellationToken.None);

        // Assert — success, draft created with the line, persisted.
        result.IsSuccess.Should().BeTrue(
            "a groupless buyer (admin) must be able to start a cart");
        result.Value.Should().NotBe(Guid.Empty);

        registered.Should().NotBeNull("the CREATE path registers a fresh draft");
        registered!.CustomerId.Should().Be(buyer.Id,
            "a self-serve buy makes the current user the buyer of record");
        registered.Status.Should().Be(SaleStatus.Draft);
        registered.LineItems.Should().ContainSingle()
            .Which.Quantity.Should().Be(2);
        unitOfWork.SaveCount.Should().Be(1);
    }

    // The APPEND path for the same groupless buyer: an existing draft
    // (e.g. from a previous add) receives the new line instead of a new
    // draft being created.
    [Fact]
    public async Task HandleAsync_WhenBuyerHasNoGroupAndDraftExists_AppendsLine()
    {
        // Arrange
        var buyer = User.CreateStaff("EMP-001", "Groupless Employee");
        var product = BuildInStockProduct();
        var draft = Sale.Create(buyer.Id, buyer.FullName, buyer.Id, buyer.FullName);
        draft.AddLineItem(product.Id, product.Name, 1, product.Price);

        var (currentUser, productRepo, saleRepo, userRepo, categoryRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, unitOfWork, logger)
            = BuildMocks(buyer, product);
        saleRepo.GetActiveDraftForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(draft);

        var command = new CreateOrAppendSaleCommand(product.Id, 3);

        // Act
        var result = await CreateOrAppendSaleCommandHandler.HandleAsync(
            command, currentUser, productRepo, saleRepo, userRepo, categoryRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock,
            unitOfWork, logger, CancellationToken.None);

        // Assert — appended to the SAME draft: the existing line for this
        // product is incremented (1 + 3 = 4), no new draft registered.
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(draft.Id,
            "the APPEND path returns the existing draft's Id");
        draft.LineItems.Should().ContainSingle()
            .Which.Quantity.Should().Be(4);
        await saleRepo.DidNotReceive().AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
        unitOfWork.SaveCount.Should().Be(1);
    }

    // Group-scoped enforcement must be SKIPPED (not mis-fired) for a
    // groupless buyer: no currency check, no budget lookup, no
    // per-product limit resolution — there is no group to scope to.
    [Fact]
    public async Task HandleAsync_WhenBuyerHasNoGroup_SkipsGroupScopedChecks()
    {
        // Arrange
        var buyer = User.CreateStaff("MGR-001", "Groupless Manager");
        var product = BuildInStockProduct();
        var (currentUser, productRepo, saleRepo, userRepo, categoryRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock, unitOfWork, logger)
            = BuildMocks(buyer, product);

        var command = new CreateOrAppendSaleCommand(product.Id, 1);

        // Act
        var result = await CreateOrAppendSaleCommandHandler.HandleAsync(
            command, currentUser, productRepo, saleRepo, userRepo, categoryRepo,
            purchaseLimitPolicy, salaryBudgetService, cartMutationLock,
            unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await purchaseLimitPolicy.DidNotReceive().IsCurrencyMatchAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await purchaseLimitPolicy.DidNotReceive().GetCountLimitAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await salaryBudgetService.DidNotReceive().GetBudgetInfoAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}

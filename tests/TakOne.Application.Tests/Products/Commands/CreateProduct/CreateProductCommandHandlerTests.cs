using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.Commands.CreateProduct;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Products.ValueObjects;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;
using DomainProduct = TakOne.Domain.Products.Entities.Product;

namespace TakOne.Application.Tests.Products.Commands.CreateProduct;

/// <summary>
/// Unit tests for <see cref="CreateProductCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, three repositories, the unit of
/// work, a logger, and a cancellation token. We mock every collaborator
/// with NSubstitute and assert on the returned <see cref="Result{T}"/>
/// plus received calls on the mocks.
/// </summary>
public class CreateProductCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — every field passes the handler's guards
    // AND the validator's rules. Each test then mutates one field to
    // exercise a single rejection path.
    private static CreateProductCommand BuildValidCommand(
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null,
        IReadOnlyList<PurchaseLimitInputDto>? purchaseLimits = null)
    {
        return new CreateProductCommand(
            Name: "Apple",
            Description: "A red apple",
            PictureUrl: null,
            Price: new MoneyDto { Amount = 1.5m, Currency = "USD" },
            InitialStockQuantity: 10,
            CategoryId: categoryId ?? TestValues.CategoryId,
            SubCategoryId: subCategoryId,
            SubSubCategoryId: subSubCategoryId,
            PurchaseLimits: purchaseLimits);
    }

    // Builds a fully-wired NSubstitute environment for the handler:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - productRepository.NameExistsAsync returns false
    //   - categoryRepository.ExistsAsync returns true
    //   - categoryRepository.SubCategoryBelongsToCategoryAsync returns true
    //   - categoryRepository.SubSubCategoryBelongsToSubCategoryAsync returns true
    //   - customerGroupRepository.GetAllAsync returns empty list (no active groups)
    //   - unitOfWork.SaveChangesAsync returns 1
    // Each test receives the tuple and can override individual mock calls
    // to exercise a specific rejection path.
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        ICategoryRepository categoryRepo,
        ICustomerGroupRepository groupRepo,
        IUnitOfWork unitOfWork,
        ILogger<CreateProductCommandHandler> logger)
        BuildMocks()
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(false);

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.ExistsAsync(default, default)
            .ReturnsForAnyArgs(true);
        categoryRepo.SubCategoryBelongsToCategoryAsync(default, default, default)
            .ReturnsForAnyArgs(true);
        categoryRepo.SubSubCategoryBelongsToSubCategoryAsync(default, default, default)
            .ReturnsForAnyArgs(true);

        var groupRepo = Substitute.For<ICustomerGroupRepository>();
        // Default: no active groups — the Phase-1 loop is a no-op.
        groupRepo.GetAllAsync(default, default)
            .ReturnsForAnyArgs(new List<CustomerGroup>());

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<CreateProductCommandHandler>>();

        return (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_ReturnsSuccessWithNewProductId()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsAddAsyncOnce()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        await CreateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await productRepo.Received(1).AddAsync(
            Arg.Any<DomainProduct>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        await CreateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenSubCategoryIdSetWithoutSubSub_Succeeds()
    {
        // Arrange
        // Sub without SubSub is a valid configuration (SubSub is optional).
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        var command = BuildValidCommand(
            subCategoryId: TestValues.SubCategoryId,
            subSubCategoryId: null);

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await categoryRepo.Received(1).SubCategoryBelongsToCategoryAsync(
            TestValues.CategoryId, TestValues.SubCategoryId, Arg.Any<CancellationToken>());
        // SubSub check must NOT be called when SubSubCategoryId is null.
        await categoryRepo.DidNotReceive().SubSubCategoryBelongsToSubCategoryAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── Auth rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        // Auth rejection must short-circuit BEFORE any repo call.
        await productRepo.DidNotReceive().NameExistsAsync(
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        // IsAuthenticated=true but UserId=Guid.Empty is still rejected —
        // the second branch of the auth check catches the missing id.
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
    }

    // ── Name uniqueness ────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNameAlreadyExists_ReturnsAlreadyExistsFailure()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        productRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(true);

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
    }

    // ── Category hierarchy ──────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCategoryDoesNotExist_ReturnsCategoryNotFoundFailure()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        categoryRepo.ExistsAsync(default, default)
            .ReturnsForAnyArgs(false);

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Category");
        result.Error.Should().Contain("was not found.");
    }

    [Fact]
    public async Task HandleAsync_WhenSubDoesNotBelongToCategory_ReturnsSubDoesNotBelongFailure()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        categoryRepo.SubCategoryBelongsToCategoryAsync(default, default, default)
            .ReturnsForAnyArgs(false);
        var command = BuildValidCommand(subCategoryId: TestValues.SubCategoryId);

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("SubCategory");
        result.Error.Should().Contain("does not belong to Category");
    }

    [Fact]
    public async Task HandleAsync_WhenSubSubDoesNotBelongToSub_ReturnsSubSubDoesNotBelongFailure()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        categoryRepo.SubSubCategoryBelongsToSubCategoryAsync(default, default, default)
            .ReturnsForAnyArgs(false);
        var command = BuildValidCommand(
            subCategoryId: TestValues.SubCategoryId,
            subSubCategoryId: TestValues.SubSubCategoryId);

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("SubSubCategory");
        result.Error.Should().Contain("does not belong to SubCategory");
    }

    // ── Duplicate purchase-limit entries ────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenPurchaseLimitsHaveDuplicateGroups_ReturnsDuplicateFailure()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        // Two entries with the SAME GroupId — Phase 2's deduplication
        // check fires BEFORE any of Phase 1's defaults are applied.
        var limits = new[]
        {
            new PurchaseLimitInputDto { GroupId = TestValues.GroupId, Limit = 5 },
            new PurchaseLimitInputDto { GroupId = TestValues.GroupId, Limit = 10 },
        };
        var command = BuildValidCommand(purchaseLimits: limits);

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Duplicate purchase limit for the same group");
    }

    [Fact]
    public async Task HandleAsync_WhenPurchaseLimitsHaveDistinctGroups_Succeeds()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        // Two entries with DIFFERENT GroupIds — no duplicate, the Phase 2
        // check passes and each is applied.
        var limits = new[]
        {
            new PurchaseLimitInputDto { GroupId = TestValues.GroupId, Limit = 5 },
            new PurchaseLimitInputDto { GroupId = TestValues.GroupId2, Limit = 10 },
        };
        var command = BuildValidCommand(purchaseLimits: limits);

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    // ── Active groups (Phase 1) ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNoActiveGroups_SucceedsAndAppliesNoDefaults()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        // Default mock returns an empty list — Phase 1 is a no-op.

        // Act
        var result = await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await groupRepo.Received(1).GetAllAsync(
            Arg.Is<bool>(b => b == false),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenActiveGroupsPresent_AppliesDefaultForEachGroup()
    {
        // Arrange
        // Two active CustomerGroups — the handler must call
        // product.SetPurchaseLimit for each (with DefaultLimit=1). We
        // verify by capturing the Product passed to AddAsync and
        // inspecting its PurchaseLimits collection.
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        var group1 = CustomerGroup.Create("Group A", new Money(10000m, TestValues.IRR));
        var group2 = CustomerGroup.Create("Group B", new Money(20000m, TestValues.IRR));
        groupRepo.GetAllAsync(default, default)
            .ReturnsForAnyArgs(new List<CustomerGroup> { group1, group2 });

        // Capture the Product passed to AddAsync.
        DomainProduct? captured = null;
        await productRepo.AddAsync(
            Arg.Do<DomainProduct>(p => captured = p),
            Arg.Any<CancellationToken>());

        // Act
        await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        captured.Should().NotBeNull();
        var limits = captured!.PurchaseLimits;
        limits.Should().HaveCount(2);
        limits.Should().Contain(l => l.GroupId == group1.Id && l.Limit == CustomerGroupPurchaseLimit.DefaultLimit);
        limits.Should().Contain(l => l.GroupId == group2.Id && l.Limit == CustomerGroupPurchaseLimit.DefaultLimit);
    }

    [Fact]
    public async Task HandleAsync_WhenActiveGroupsPresentAndUserOverrides_OverridesReplaceDefaults()
    {
        // Arrange
        // Phase 1 sets a default (limit=1) for each active group; Phase 2
        // overrides the user-specified entries. The captured Product's
        // PurchaseLimits must reflect the user-specified value, not the
        // default, for groups the user listed.
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        var group1 = CustomerGroup.Create("Group A", new Money(10000m, TestValues.IRR));
        groupRepo.GetAllAsync(default, default)
            .ReturnsForAnyArgs(new List<CustomerGroup> { group1 });

        // User-specified override: limit=5 for group1.
        var limits = new[]
        {
            new PurchaseLimitInputDto { GroupId = group1.Id, Limit = 5 },
        };
        var command = BuildValidCommand(purchaseLimits: limits);

        // Capture the Product passed to AddAsync.
        DomainProduct? captured = null;
        await productRepo.AddAsync(
            Arg.Do<DomainProduct>(p => captured = p),
            Arg.Any<CancellationToken>());

        // Act
        await CreateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        captured.Should().NotBeNull();
        var capturedLimits = captured!.PurchaseLimits;
        capturedLimits.Should().ContainSingle(l => l.GroupId == group1.Id);
        capturedLimits.First(l => l.GroupId == group1.Id).Limit.Should().Be(5);
    }

    // ── Cancellation token forwarding ───────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToNameExistsAsync()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            ct);

        // Assert
        await productRepo.Received(1).NameExistsAsync(
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToCategoryExistsAsync()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            ct);

        // Assert
        await categoryRepo.Received(1).ExistsAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToGetAllAsync()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            ct);

        // Assert
        await groupRepo.Received(1).GetAllAsync(
            Arg.Any<bool>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToSaveChangesAsync()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await CreateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, logger,
            ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.Commands.UpdateProductCategory;
using TakOne.Domain.Products.Entities;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.UpdateProductCategory;

/// <summary>
/// Unit tests for <see cref="UpdateProductCategoryCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the product repository, the
/// category repository, the unit of work, a logger, and a cancellation
/// token. We mock every collaborator with NSubstitute. The repository
/// returns a REAL <see cref="Product"/> instance (built via
/// <see cref="ProductBuilder"/>) so we can observe the side-effect of
/// <c>UpdateCategory</c> on the <c>CategoryId/SubCategoryId/SubSubCategoryId</c>
/// properties.
///
/// SPECIAL FOCUS: all five hierarchy-validation branches are exercised
/// (category missing; subcategory-doesn't-belong; subsubcategory-doesn't-
/// belong; subsubcategory-without-subcategory handled by domain; happy
/// path with full hierarchy).
/// </summary>
public class UpdateProductCategoryCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command. SubCategoryId/SubSubCategoryId default to
    // TestValues.X for the FULL-HIERARCHY happy path (so tests like
    // "WhenAllChecksPass" exercise all three category-repo checks).
    // Tests that need to clear one or both pass null explicitly — the
    // helper passes through.
    private static UpdateProductCategoryCommand BuildValidCommand(
        Guid? productId = null,
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            CategoryId: categoryId ?? TestValues.CategoryId,
            SubCategoryId: subCategoryId,
            SubSubCategoryId: subSubCategoryId);

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - productRepository.GetByIdAsync returns a real Product instance
    //   - categoryRepository.ExistsAsync returns true
    //   - categoryRepository.SubCategoryBelongsToCategoryAsync returns true
    //   - categoryRepository.SubSubCategoryBelongsToSubCategoryAsync returns true
    //   - unitOfWork.SaveChangesAsync returns 1
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        ICategoryRepository categoryRepo,
        IUnitOfWork unitOfWork,
        ILogger<UpdateProductCategoryCommandHandler> logger,
        Product product)
        BuildMocks(Product? product = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a real Product (not a mock) so we can observe the
        // UpdateCategory side-effect on CategoryId/SubCategoryId/
        // SubSubCategoryId after the handler runs.
        var actualProduct = product ?? new ProductBuilder()
            .WithName("Categorizable Product")
            .WithStock(10)
            .Build();

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualProduct);

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.ExistsAsync(default, default)
            .ReturnsForAnyArgs(true);
        categoryRepo.SubCategoryBelongsToCategoryAsync(default, default, default)
            .ReturnsForAnyArgs(true);
        categoryRepo.SubSubCategoryBelongsToSubCategoryAsync(default, default, default)
            .ReturnsForAnyArgs(true);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<UpdateProductCategoryCommandHandler>>();

        return (currentUser, productRepo, categoryRepo, unitOfWork, logger, actualProduct);
    }

    // ── Happy path (full hierarchy) ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        var result = await UpdateProductCategoryCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_UpdatesProductCategoryFields()
    {
        // Arrange
        // The handler delegates to product.UpdateCategory(categoryId,
        // subCategoryId, subSubCategoryId). A real Product's
        // CategoryId/SubCategoryId/SubSubCategoryId reflect the new values
        // after the handler runs — proving UpdateCategory was called.
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, product) = BuildMocks();
        var command = BuildValidCommand(
            categoryId: TestValues.CategoryId,
            subCategoryId: TestValues.SubCategoryId,
            subSubCategoryId: TestValues.SubSubCategoryId);

        // Act
        await UpdateProductCategoryCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        product.CategoryId.Should().Be(TestValues.CategoryId);
        product.SubCategoryId.Should().Be(TestValues.SubCategoryId);
        product.SubSubCategoryId.Should().Be(TestValues.SubSubCategoryId);
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_LogsInformation()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
                logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Auth rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        await productRepo.DidNotReceive().GetByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
    }

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
                logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Not found (product missing) ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenProductNotFound_ReturnsFailureWithProductId()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Product?)null);

        // Act
        var result = await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Product '{TestValues.ProductId}' was not found.");
    }

    [Fact]
    public async Task HandleAsync_WhenProductNotFound_LogsWarning()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Product?)null);

        // Act
        await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
                logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Hierarchy validation: category missing ─────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCategoryDoesNotExist_ReturnsCategoryNotFoundFailure()
    {
        // Arrange
        // Override categoryRepo.ExistsAsync to return false — the
        // top-level category doesn't exist. The handler must fail fast
        // before even checking subcategory membership.
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        categoryRepo.ExistsAsync(default, default)
            .ReturnsForAnyArgs(false);

        // Act
        var result = await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Category '{TestValues.CategoryId}' was not found.");
        // The subcategory check must NOT fire when the top-level
        // category is missing — fail fast at the top level.
        await categoryRepo.DidNotReceive().SubCategoryBelongsToCategoryAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── Hierarchy validation: subcategory doesn't belong to category ──

    [Fact]
    public async Task HandleAsync_WhenSubDoesNotBelongToCategory_ReturnsSubDoesNotBelongFailure()
    {
        // Arrange
        // categoryRepo.ExistsAsync returns true (default), but
        // SubCategoryBelongsToCategoryAsync returns false — the
        // subcategory is a real SubCategory, just one that belongs to
        // a different top-level Category.
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        categoryRepo.SubCategoryBelongsToCategoryAsync(default, default, default)
            .ReturnsForAnyArgs(false);

        // Act
        var result = await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(subCategoryId: TestValues.SubCategoryId),
            currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(
            $"SubCategory '{TestValues.SubCategoryId}' does not belong to Category '{TestValues.CategoryId}'.");
        // The subsubcategory check must NOT fire when the subcategory
        // doesn't belong — we short-circuit at the first failed level.
        await categoryRepo.DidNotReceive().SubSubCategoryBelongsToSubCategoryAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── Hierarchy validation: subsubcategory doesn't belong to sub ─────

    [Fact]
    public async Task HandleAsync_WhenSubSubDoesNotBelongToSub_ReturnsSubSubDoesNotBelongFailure()
    {
        // Arrange
        // Top-level category exists, sub belongs to category, but
        // subsub doesn't belong to the sub — the subsub is a real
        // SubSubCategory, just one that belongs to a different Sub.
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        categoryRepo.SubSubCategoryBelongsToSubCategoryAsync(default, default, default)
            .ReturnsForAnyArgs(false);
        var command = BuildValidCommand(
            subCategoryId: TestValues.SubCategoryId,
            subSubCategoryId: TestValues.SubSubCategoryId);

        // Act
        var result = await UpdateProductCategoryCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(
            $"SubSubCategory '{TestValues.SubSubCategoryId}' does not belong to SubCategory '{TestValues.SubCategoryId}'.");
    }

    // ── Hierarchy validation: only top-level category (no sub) ────────

    [Fact]
    public async Task HandleAsync_WhenOnlyCategoryIdIsSet_SkipsSubAndSubSubChecks()
    {
        // Arrange
        // When SubCategoryId is null, the handler skips both the
        // SubCategoryBelongsToCategory and SubSubCategoryBelongsToSub
        // checks — this is the "move to top-level only" case.
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, product) = BuildMocks();
        var command = BuildValidCommand(
            categoryId: TestValues.CategoryId,
            subCategoryId: null,
            subSubCategoryId: null);

        // Act
        var result = await UpdateProductCategoryCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // Sub check must NOT have fired because SubCategoryId was null.
        await categoryRepo.DidNotReceive().SubCategoryBelongsToCategoryAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        // SubSub check must NOT have fired (also because SubCategoryId
        // was null — the SubSub check is nested inside the Sub null check).
        await categoryRepo.DidNotReceive().SubSubCategoryBelongsToSubCategoryAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        // The product's SubCategoryId and SubSubCategoryId must be null
        // after the handler runs (UpdateCategory clears them).
        product.SubCategoryId.Should().BeNull();
        product.SubSubCategoryId.Should().BeNull();
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToGetByIdAsync()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await productRepo.Received(1).GetByIdAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToCategoryExistsAsync()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await categoryRepo.Received(1).ExistsAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToSaveChangesAsync()
    {
        // Arrange
        var (currentUser, productRepo, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await UpdateProductCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

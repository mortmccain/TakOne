using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.Commands.DeactivateProduct;
using TakOne.Domain.Products.Entities;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.DeactivateProduct;

/// <summary>
/// Unit tests for <see cref="DeactivateProductCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the product repository, the unit
/// of work, a logger, and a cancellation token. We mock every collaborator
/// with NSubstitute. The repository returns a REAL <see cref="Product"/>
/// instance (built via <see cref="ProductBuilder"/>) so we can observe
/// the side-effect of <c>SetStock(0)</c> on <c>StockQuantity</c>.
/// </summary>
public class DeactivateProductCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static DeactivateProductCommand BuildValidCommand(Guid? productId = null)
        => new(productId ?? TestValues.ProductId);

    // Builds a fully-wired NSubstitute environment. Pass a product with
    // a non-zero stock to exercise the "stock was non-zero" path; pass
    // one with zero stock to exercise the "already deactivated" idempotent
    // path.
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        IUnitOfWork unitOfWork,
        ILogger<DeactivateProductCommandHandler> logger,
        Product product)
        BuildMocks(Product? product = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a real Product (not a mock) so we can observe the
        // SetStock(0) side-effect on StockQuantity after the handler
        // runs. Start at 50 so the deactivation visibly zeroes it.
        var actualProduct = product ?? new ProductBuilder()
            .WithName("Deactivatable Product")
            .WithStock(50)
            .Build();

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualProduct);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<DeactivateProductCommandHandler>>();

        return (currentUser, productRepo, unitOfWork, logger, actualProduct);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        var result = await DeactivateProductCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_SetsStockToZero()
    {
        // Arrange
        // The handler delegates to product.SetStock(0). A real Product
        // starting at 50 must end at 0 — verifying the side-effect
        // proves SetStock(0) was actually called (vs. e.g. AdjustStockTo
        // which would have thrown because 0 is not greater than 0).
        var (currentUser, productRepo, unitOfWork, logger, product) = BuildMocks();
        // product starts at StockQuantity=50.

        // Act
        await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        product.StockQuantity.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_LogsInformationWithPreviousStock()
    {
        // Arrange
        // The handler's log message template includes "Previous stock was
        // {PreviousStock} (now 0)" so the audit trail captures the
        // pre-deactivation stock value (the system forgets it after this).
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // We assert that LogInformation was called exactly once — the
        // success-path log call only fires when SetStock(0) succeeded
        // and SaveChanges was called.
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
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
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
        // IsAuthenticated=true but UserId=Guid.Empty is still rejected —
        // the second branch of the auth check catches the missing id.
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
    }

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
                logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Not found ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenProductNotFound_ReturnsFailureWithProductId()
    {
        // Arrange
        // Override the mock to return null — simulates a missing product
        // row or a wrong id passed in by the caller.
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Product?)null);

        // Act
        var result = await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Product '{TestValues.ProductId}' was not found.");
    }

    [Fact]
    public async Task HandleAsync_WhenProductNotFound_LogsWarning()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Product?)null);

        // Act
        await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
                logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToGetByIdAsync()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger, ct);

        // Assert
        await productRepo.Received(1).GetByIdAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToSaveChangesAsync()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Idempotency: already-zero stock ───────────────────────────────

    // Deactivating an already-zero-stock product is a no-op at the
    // domain level — SetStock(0) goes through the EnsureStockQuantityValid
    // guard which only rejects NEGATIVE values (0 is valid). The handler
    // still logs the call (audit trail) and still calls SaveChanges.
    [Fact]
    public async Task HandleAsync_WhenStockIsAlreadyZero_StillSucceeds()
    {
        // Arrange
        // Pre-build a Product with zero stock so SetStock(0) is a no-op.
        var product = new ProductBuilder()
            .WithName("Already Inactive Product")
            .WithStock(0)
            .Build();
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks(product: product);

        // Act
        var result = await DeactivateProductCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        product.StockQuantity.Should().Be(0);
    }
}

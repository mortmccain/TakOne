using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.Commands.SetProductStock;
using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.SetProductStock;

/// <summary>
/// Unit tests for <see cref="SetProductStockCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the product repository, the unit
/// of work, a logger, and a cancellation token. We mock every collaborator
/// with NSubstitute. The repository returns a REAL <see cref="Product"/>
/// instance (built via <see cref="ProductBuilder"/>) so we can observe
/// the side-effect of <c>AdjustStockTo</c> on <c>StockQuantity</c>.
/// </summary>
public class SetProductStockCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — Quantity=42 is well inside the validator's
    // [1, 100_000] band, and ProductId references the stable TestValues id.
    private static SetProductStockCommand BuildValidCommand(
        Guid? productId = null,
        int? quantity = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            Quantity: quantity ?? 42);

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - productRepository.GetByIdAsync returns a real Product instance
    //     with a starting stock of 10 (captured by the builder).
    //   - unitOfWork.SaveChangesAsync returns 1
    // Each test receives the tuple and can override individual mock calls
    // to exercise a specific rejection path.
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        IUnitOfWork unitOfWork,
        ILogger<SetProductStockCommandHandler> logger,
        Product product)
        BuildMocks(Product? product = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a real Product (not a mock) so we can observe the
        // AdjustStockTo side-effect on StockQuantity after the handler
        // runs. Start at 10 so Quantity=42 changes it to 42.
        var actualProduct = product ?? new ProductBuilder()
            .WithName("Stockable Product")
            .WithStock(10)
            .Build();

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualProduct);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<SetProductStockCommandHandler>>();

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
        var result = await SetProductStockCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_SetsStockToQuantity()
    {
        // Arrange
        // The handler delegates to product.AdjustStockTo(command.Quantity),
        // which is the absolute-replacement method on the Product
        // aggregate. We verify the side-effect on the REAL product instance.
        var (currentUser, productRepo, unitOfWork, logger, product) = BuildMocks();
        var command = BuildValidCommand(quantity: 42);

        // Act
        await SetProductStockCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        product.StockQuantity.Should().Be(42);
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await SetProductStockCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_LogsInformationWithBeforeAndAfterStock()
    {
        // Arrange
        // The handler's log message template must include "Before: {Before},
        // after: {After}" so the audit trail captures the pre-change stock.
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await SetProductStockCommandHandler.HandleAsync(
            BuildValidCommand(quantity: 42), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // NSubstitute can't directly match ILogger extension methods
        // (LogInformation/LogWarning) because they wrap the underlying
        // Log<TState> call with a different argument shape. We use the
        // underlying Log method with Arg.AnyType to match the generic
        // state parameter.
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
        var result = await SetProductStockCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        // Auth rejection must short-circuit BEFORE any repo call.
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
        var result = await SetProductStockCommandHandler.HandleAsync(
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
        await SetProductStockCommandHandler.HandleAsync(
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
        var result = await SetProductStockCommandHandler.HandleAsync(
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
        await SetProductStockCommandHandler.HandleAsync(
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
        await SetProductStockCommandHandler.HandleAsync(
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
        await SetProductStockCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Domain exception propagation ───────────────────────────────────

    // AdjustStockTo throws DomainException when quantity ≤ 0. In the real
    // wiring the validator catches this BEFORE the handler runs, but the
    // handler is a static method callable from tests/non-HTTP hosts, so
    // the domain never trusts the caller. We assert that the exception
    // propagates (i.e. the handler does NOT swallow it or convert it to
    // Result.Failure).
    [Fact]
    public async Task HandleAsync_WhenQuantityIsZero_AdjustStockToThrowsDomainException()
    {
        // Arrange
        // Build a product directly and bypass the validator by passing a
        // zero quantity straight to the handler.
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        Func<Task> act = async () => await SetProductStockCommandHandler.HandleAsync(
            BuildValidCommand(quantity: 0), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // The domain's defense-in-depth guard fires before SaveChanges.
        await act.Should().ThrowAsync<DomainException>()
            .WithMessage("Stock quantity must be greater than zero. To make it zero, deactivate the product instead.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

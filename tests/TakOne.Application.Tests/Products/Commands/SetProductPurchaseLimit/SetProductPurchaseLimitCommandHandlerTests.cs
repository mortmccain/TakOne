using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.Commands.SetProductPurchaseLimit;
using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.SetProductPurchaseLimit;

/// <summary>
/// Unit tests for <see cref="SetProductPurchaseLimitCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the product repository, the unit
/// of work, a logger, and a cancellation token. We mock every collaborator
/// with NSubstitute. The repository returns a REAL <see cref="Product"/>
/// instance (built via <see cref="ProductBuilder"/>) so we can observe
/// the side-effect of <c>SetPurchaseLimit</c> on the
/// <c>PurchaseLimits</c> collection.
/// </summary>
public class SetProductPurchaseLimitCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static SetProductPurchaseLimitCommand BuildValidCommand(
        Guid? productId = null,
        Guid? groupId = null,
        int? limit = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            GroupId: groupId ?? TestValues.GroupId,
            Limit: limit ?? 5);

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - productRepository.GetByIdAsync returns a real Product instance
    //     with no existing purchase limits.
    //   - unitOfWork.SaveChangesAsync returns 1
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        IUnitOfWork unitOfWork,
        ILogger<SetProductPurchaseLimitCommandHandler> logger,
        Product product)
        BuildMocks(Product? product = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a real Product (not a mock) so we can observe the
        // SetPurchaseLimit side-effect on PurchaseLimits after the handler
        // runs. A fresh Product has no purchase limits, so the call adds
        // one entry.
        var actualProduct = product ?? new ProductBuilder()
            .WithName("Limitable Product")
            .WithStock(10)
            .Build();

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualProduct);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<SetProductPurchaseLimitCommandHandler>>();

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
        var result = await SetProductPurchaseLimitCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_AddsPurchaseLimitToProduct()
    {
        // Arrange
        // The handler delegates to product.SetPurchaseLimit(groupId, limit),
        // which adds a new CustomerGroupPurchaseLimit to the Product's
        // PurchaseLimits collection. A fresh Product has zero entries, so
        // after the handler runs we expect exactly one entry for GroupId.
        var (currentUser, productRepo, unitOfWork, logger, product) = BuildMocks();
        var command = BuildValidCommand(groupId: TestValues.GroupId, limit: 7);

        // Act
        await SetProductPurchaseLimitCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        product.PurchaseLimits.Should().ContainSingle();
        var added = product.PurchaseLimits.Single();
        added.GroupId.Should().Be(TestValues.GroupId);
        added.Limit.Should().Be(7);
    }

    [Fact]
    public async Task HandleAsync_WhenLimitAlreadyExistsForGroup_ReplacesOldLimit()
    {
        // Arrange
        // Product.SetPurchaseLimit removes any existing limit for the
        // same group, then adds the new one (so the count stays at 1,
        // not 2, when the same group is set twice). We pre-seed the
        // Product with a limit of 3, then have the handler set it to 5.
        var product = new ProductBuilder().WithName("Limitable Product").WithStock(10).Build();
        product.SetPurchaseLimit(TestValues.GroupId, 3);
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks(product: product);
        var command = BuildValidCommand(groupId: TestValues.GroupId, limit: 5);

        // Act
        await SetProductPurchaseLimitCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // Replacing the limit must NOT add a second entry — the
        // Product aggregate enforces "one limit per group" by removing
        // the old one before adding the new one.
        product.PurchaseLimits.Should().ContainSingle();
        product.PurchaseLimits.Single().Limit.Should().Be(5);
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await SetProductPurchaseLimitCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_LogsInformation()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await SetProductPurchaseLimitCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
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
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await SetProductPurchaseLimitCommandHandler.HandleAsync(
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
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await SetProductPurchaseLimitCommandHandler.HandleAsync(
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
        await SetProductPurchaseLimitCommandHandler.HandleAsync(
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
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Product?)null);

        // Act
        var result = await SetProductPurchaseLimitCommandHandler.HandleAsync(
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
        await SetProductPurchaseLimitCommandHandler.HandleAsync(
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
        await SetProductPurchaseLimitCommandHandler.HandleAsync(
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
        await SetProductPurchaseLimitCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Domain exception propagation ───────────────────────────────────

    // SetPurchaseLimit throws DomainException when groupId is Guid.Empty.
    // In the real wiring the validator catches this BEFORE the handler
    // runs, but the handler is a static method callable from tests/non-
    // HTTP hosts, so the domain never trusts the caller.
    [Fact]
    public async Task HandleAsync_WhenGroupIdIsEmpty_SetPurchaseLimitThrowsDomainException()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        // Bypass the validator by passing an empty GroupId directly to
        // the handler — defense-in-depth: the domain must reject it
        // even when the validator is bypassed.
        var command = new SetProductPurchaseLimitCommand(
            ProductId: TestValues.ProductId,
            GroupId: Guid.Empty,
            Limit: 5);

        // Act
        Func<Task> act = async () => await SetProductPurchaseLimitCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .WithMessage("Group Id is required to set a purchase limit.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

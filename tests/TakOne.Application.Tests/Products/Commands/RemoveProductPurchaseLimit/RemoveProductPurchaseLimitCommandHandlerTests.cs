using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.Commands.RemoveProductPurchaseLimit;
using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.RemoveProductPurchaseLimit;

/// <summary>
/// Unit tests for <see cref="RemoveProductPurchaseLimitCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the product repository, the unit
/// of work, a logger, and a cancellation token. We mock every collaborator
/// with NSubstitute. The repository returns a REAL <see cref="Product"/>
/// instance (built via <see cref="ProductBuilder"/>) so we can observe
/// the side-effect of <c>RemovePurchaseLimit</c> on the
/// <c>PurchaseLimits</c> collection — including the IDEMPOTENT no-op
/// case (limit doesn't exist for the group; the handler still succeeds).
/// </summary>
public class RemoveProductPurchaseLimitCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static RemoveProductPurchaseLimitCommand BuildValidCommand(
        Guid? productId = null,
        Guid? groupId = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            GroupId: groupId ?? TestValues.GroupId);

    // Builds a fully-wired NSubstitute environment. Pass a pre-seeded
    // Product via the optional parameter to exercise the "limit exists"
    // path; leave null to exercise the "limit doesn't exist" idempotent
    // path.
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        IUnitOfWork unitOfWork,
        ILogger<RemoveProductPurchaseLimitCommandHandler> logger,
        Product product)
        BuildMocks(Product? product = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a real Product (not a mock) so we can observe the
        // RemovePurchaseLimit side-effect on PurchaseLimits after the
        // handler runs.
        var actualProduct = product ?? new ProductBuilder()
            .WithName("Limitable Product")
            .WithStock(10)
            .Build();

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualProduct);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<RemoveProductPurchaseLimitCommandHandler>>();

        return (currentUser, productRepo, unitOfWork, logger, actualProduct);
    }

    // ── Happy path (limit exists) ──────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenLimitExistsForGroup_ReturnsSuccess()
    {
        // Arrange
        // Pre-seed the Product with a limit for GroupId so the
        // RemovePurchaseLimit call has something to remove.
        var product = new ProductBuilder().WithName("Limitable Product").WithStock(10).Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks(product: product);

        // Act
        var result = await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenLimitExistsForGroup_RemovesTheLimit()
    {
        // Arrange
        // Pre-seed with TWO limits (GroupId and GroupId2) — verify that
        // removing GroupId's limit leaves GroupId2's limit alone. This
        // guards against an accidental "clear all limits" refactor.
        var product = new ProductBuilder().WithName("Limitable Product").WithStock(10).Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);
        product.SetPurchaseLimit(TestValues.GroupId2, 10);
        var (currentUser, productRepo, unitOfWork, logger, capturedProduct) = BuildMocks(product: product);

        // Act
        await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
            BuildValidCommand(groupId: TestValues.GroupId),
            currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        capturedProduct.PurchaseLimits.Should().ContainSingle();
        capturedProduct.PurchaseLimits.Single().GroupId.Should().Be(TestValues.GroupId2);
    }

    // ── Idempotent no-op path (limit doesn't exist) ────────────────────

    // Product.RemovePurchaseLimit is idempotent — if no limit exists for
    // the given group, it's a no-op (doesn't throw). The handler still
    // succeeds and still calls SaveChangesAsync (a null-op round-trip
    // at the EF Core level — see the SUT's inline comment).
    [Fact]
    public async Task HandleAsync_WhenNoLimitExistsForGroup_StillSucceeds()
    {
        // Arrange
        // Fresh Product with NO limits — the handler must NOT throw,
        // and the result must be a success.
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        var result = await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
            BuildValidCommand(groupId: TestValues.GroupId),
            currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenNoLimitExistsForGroup_StillCallsSaveChanges()
    {
        // Arrange
        // The handler persists unconditionally (the SUT comment says
        // "EF Core simply won't detect any changes and SaveChangesAsync
        // is a null-op round-trip"). We verify SaveChangesAsync IS
        // called even in the no-op case — the handler doesn't try to
        // be clever and skip the save.
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenNoLimitExistsForGroup_LogsInformation()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // The log message says "(if it existed)" — the handler doesn't
        // know whether the limit was actually there, and the log
        // reflects that ambiguity honestly.
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
        var result = await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
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
        var result = await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
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
        await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
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
        var result = await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
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
        await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
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
        await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
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
        await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Domain exception propagation ───────────────────────────────────

    // RemovePurchaseLimit throws DomainException when groupId is Guid.Empty.
    // In the real wiring the validator catches this BEFORE the handler
    // runs, but the handler is a static method callable from tests/non-
    // HTTP hosts, so the domain never trusts the caller.
    [Fact]
    public async Task HandleAsync_WhenGroupIdIsEmpty_RemovePurchaseLimitThrowsDomainException()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        // Bypass the validator by passing an empty GroupId directly to
        // the handler.
        var command = new RemoveProductPurchaseLimitCommand(
            ProductId: TestValues.ProductId,
            GroupId: Guid.Empty);

        // Act
        Func<Task> act = async () => await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<DomainException>()
            .WithMessage("Group Id is required to remove a purchase limit.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

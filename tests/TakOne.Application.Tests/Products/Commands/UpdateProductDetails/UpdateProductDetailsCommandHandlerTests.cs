using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.Commands.UpdateProductDetails;
using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.UpdateProductDetails;

/// <summary>
/// Unit tests for <see cref="UpdateProductDetailsCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the product repository, the unit
/// of work, a logger, and a cancellation token. We mock every collaborator
/// with NSubstitute. The repository returns a REAL <see cref="Product"/>
/// instance (built via <see cref="ProductBuilder"/>) so we can observe
/// the side-effect of <c>UpdateDetails</c> on Name/Description/Price/PictureUrl.
///
/// SPECIAL FOCUS: name uniqueness violation (with the excludeId pattern
/// that allows rename-to-same-name), Money construction failure path
/// (invalid currency throws DomainException at Money ctor time), and
/// the standard auth/not-found/cancellation coverage.
/// </summary>
public class UpdateProductDetailsCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — every field passes the handler's guards
    // AND the validator's rules. The ProductId matches the stable
    // TestValues.ProductId so the not-found test can construct the
    // expected error message deterministically.
    private static UpdateProductDetailsCommand BuildValidCommand(
        Guid? productId = null,
        string? name = null,
        string? description = null,
        string? pictureUrl = null,
        decimal? amount = null,
        string? currency = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            Name: name ?? "Updated Product",
            Description: description ?? "An updated product",
            PictureUrl: pictureUrl,
            Price: new MoneyDto { Amount = amount ?? 2.5m, Currency = currency ?? "USD" });

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - productRepository.GetByIdAsync returns a real Product instance
    //     (built with a known Name so tests can detect a rename)
    //   - productRepository.NameExistsAsync returns false (no conflict)
    //   - unitOfWork.SaveChangesAsync returns 1
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        IUnitOfWork unitOfWork,
        ILogger<UpdateProductDetailsCommandHandler> logger,
        Product product)
        BuildMocks(Product? product = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a real Product (not a mock) so we can observe the
        // UpdateDetails side-effect on Name/Description/Price/PictureUrl
        // after the handler runs.
        var actualProduct = product ?? new ProductBuilder()
            .WithName("Original Product Name")
            .WithDescription("Original description")
            .WithPrice(new Money(1.0m, TestValues.USD))
            .WithStock(10)
            .Build();

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualProduct);
        // Default: no name conflict (the product's own name is excluded
        // by the excludeId pattern, and no OTHER product has the name).
        productRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(false);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<UpdateProductDetailsCommandHandler>>();

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
        var result = await UpdateProductDetailsCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_UpdatesProductFields()
    {
        // Arrange
        // The handler delegates to product.UpdateDetails(name, description,
        // price, pictureUrl). A real Product's Name/Description/Price/
        // PictureUrl reflect the new values after the handler runs.
        var (currentUser, productRepo, unitOfWork, logger, product) = BuildMocks();
        var command = BuildValidCommand(
            name: "New Name",
            description: "New description",
            pictureUrl: "https://cdn.example.com/new.png",
            amount: 9.99m,
            currency: "USD");

        // Act
        await UpdateProductDetailsCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        product.Name.Should().Be("New Name");
        product.Description.Should().Be("New description");
        product.PictureUrl.Should().Be("https://cdn.example.com/new.png");
        product.Price.Amount.Should().Be(9.99m);
        product.Price.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await UpdateProductDetailsCommandHandler.HandleAsync(
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
        await UpdateProductDetailsCommandHandler.HandleAsync(
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
        var result = await UpdateProductDetailsCommandHandler.HandleAsync(
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
        var result = await UpdateProductDetailsCommandHandler.HandleAsync(
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
        await UpdateProductDetailsCommandHandler.HandleAsync(
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
        var result = await UpdateProductDetailsCommandHandler.HandleAsync(
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
        await UpdateProductDetailsCommandHandler.HandleAsync(
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

    // ── Name uniqueness ────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNameExistsForOtherProduct_ReturnsAlreadyExistsFailure()
    {
        // Arrange
        // Override the default mock so NameExistsAsync returns true —
        // simulates ANOTHER product already using the requested name.
        // The handler must fail with the documented message.
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        productRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(true);
        var command = BuildValidCommand(name: "Already Taken Name");

        // Act
        var result = await UpdateProductDetailsCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(
            "Another product with the name 'Already Taken Name' already exists. Choose a different name.");
        // SaveChanges must NOT have been called — we failed at the
        // uniqueness check before mutating the aggregate.
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // The excludeId contract: when the caller renames the product to its
    // OWN current name (no-op rename), NameExistsAsync(name, excludeId=
    // product.Id) should return false because the only match is the
    // product itself — which is excluded. We verify that the handler
    // passes the loaded product's Id as the excludeId argument.
    [Fact]
    public async Task HandleAsync_WhenNameIsUnchanged_PassesProductOwnIdAsExcludeId()
    {
        // Arrange
        // The product is loaded by the handler with a known Id; the
        // NameExistsAsync call must use that Id as the excludeId so a
        // no-op rename (name unchanged) doesn't trip the uniqueness rule.
        var (currentUser, productRepo, unitOfWork, logger, product) = BuildMocks();
        var command = BuildValidCommand(name: "Any Name");

        // Act
        await UpdateProductDetailsCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // The excludeId parameter must be the loaded product's Id
        // (not Guid.Empty, not the command's ProductId if that differs).
        await productRepo.Received(1).NameExistsAsync(
            Arg.Any<string>(),
            Arg.Is<Guid?>(id => id == product.Id),
            Arg.Any<CancellationToken>());
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
        await UpdateProductDetailsCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger, ct);

        // Assert
        await productRepo.Received(1).GetByIdAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToNameExistsAsync()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await UpdateProductDetailsCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger, ct);

        // Assert
        await productRepo.Received(1).NameExistsAsync(
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
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
        await UpdateProductDetailsCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, productRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Money construction failure path ────────────────────────────────

    // The handler constructs the domain Money value object from the DTO:
    //   `var price = new Money(command.Price.Amount, command.Price.Currency);`
    // Money's ctor throws DomainException when currency is not exactly
    // 3 chars. In the real wiring the validator catches this BEFORE the
    // handler runs, but the handler is a static method callable from
    // tests/non-HTTP hosts, so the domain never trusts the caller.
    [Fact]
    public async Task HandleAsync_WhenCurrencyIsInvalidLength_MoneyCtorThrowsDomainException()
    {
        // Arrange
        var (currentUser, productRepo, unitOfWork, logger, _) = BuildMocks();
        // Bypass the validator by passing a 2-char currency directly
        // to the handler — defense-in-depth: Money's ctor must reject
        // it even when the validator is bypassed.
        var command = BuildValidCommand(currency: "US");

        // Act
        Func<Task> act = async () => await UpdateProductDetailsCommandHandler.HandleAsync(
            command, currentUser, productRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // The Money ctor throws before the handler can call UpdateDetails
        // or SaveChanges.
        await act.Should().ThrowAsync<DomainException>()
            .WithMessage("Currency must be a 3-letter ISO code.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}

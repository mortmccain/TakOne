using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Categories.Commands.CreateCategory;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.CreateCategory;

/// <summary>
/// Unit tests for <see cref="CreateCategoryCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the category repository, the unit
/// of work, a logger, and a cancellation token. We mock every collaborator
/// with NSubstitute and assert on the returned <see cref="Result{T}"/>
/// plus received calls on the mocks.
/// </summary>
public class CreateCategoryCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static (ICurrentUserService currentUser, ICategoryRepository categoryRepo, IUnitOfWork unitOfWork, ILogger<CreateCategoryCommandHandler> logger)
        BuildMocks()
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        var categoryRepo = Substitute.For<ICategoryRepository>();
        // Default: name does NOT already exist.
        categoryRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(false);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<CreateCategoryCommandHandler>>();

        return (currentUser, categoryRepo, unitOfWork, logger);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNameDoesNotExist_ReturnsSuccessWithNewCategoryId()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        var command = new CreateCategoryCommand("Books");

        // Act
        var result = await CreateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task HandleAsync_WhenNameDoesNotExist_CallsAddAsyncOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        var command = new CreateCategoryCommand("Books");

        // Act
        await CreateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await categoryRepo.Received(1).AddAsync(
            Arg.Any<Category>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenNameDoesNotExist_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        var command = new CreateCategoryCommand("Books");

        // Act
        await CreateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenNameDoesNotExist_PassesCorrectNameToCategoryCreate()
    {
        // Arrange
        // Capture the Category passed to AddAsync and verify its Name
        // matches the command's Name (the handler passes the name straight
        // through to Category.Create).
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        const string expectedName = "Books & Magazines";
        var command = new CreateCategoryCommand(expectedName);

        Category? captured = null;
        await categoryRepo.AddAsync(
            Arg.Do<Category>(c => captured = c),
            Arg.Any<CancellationToken>());

        // Act
        await CreateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        captured.Should().NotBeNull();
        captured!.Name.Should().Be(expectedName);
        captured.Id.Should().NotBe(Guid.Empty);
    }

    // ── Auth rejection ──────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await CreateCategoryCommandHandler.HandleAsync(
            new CreateCategoryCommand("Books"), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        // Auth rejection must short-circuit BEFORE the repo call.
        await categoryRepo.DidNotReceive().NameExistsAsync(
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await CreateCategoryCommandHandler.HandleAsync(
            new CreateCategoryCommand("Books"), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
    }

    // ── Name uniqueness ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNameAlreadyExists_ReturnsAlreadyExistsFailure()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        categoryRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(true);

        // Act
        var result = await CreateCategoryCommandHandler.HandleAsync(
            new CreateCategoryCommand("Books"), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
    }

    [Fact]
    public async Task HandleAsync_WhenNameAlreadyExists_DoesNotCallAddAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        categoryRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(true);

        // Act
        await CreateCategoryCommandHandler.HandleAsync(
            new CreateCategoryCommand("Books"), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // The duplicate-name rejection must short-circuit before persistence.
        await categoryRepo.DidNotReceive().AddAsync(
            Arg.Any<Category>(), Arg.Any<CancellationToken>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Cancellation token forwarding ───────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToNameExistsAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await CreateCategoryCommandHandler.HandleAsync(
            new CreateCategoryCommand("Books"), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await categoryRepo.Received(1).NameExistsAsync(
            Arg.Any<string>(),
            Arg.Any<Guid?>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToSaveChangesAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await CreateCategoryCommandHandler.HandleAsync(
            new CreateCategoryCommand("Books"), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

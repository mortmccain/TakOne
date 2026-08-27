using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Categories.Commands.RenameCategory;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.RenameCategory;

/// <summary>
/// Unit tests for <see cref="RenameCategoryCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the category repository, the
/// unit of work, a logger, and a cancellation token. We mock every
/// collaborator with NSubstitute. The repository returns a REAL
/// <see cref="Category"/> instance (built via <see cref="Category.Create"/>)
/// so we can observe the side-effect of <c>Rename()</c> on
/// <c>Name</c>.
///
/// SPECIAL CASE: the handler calls
/// <c>categoryRepository.NameExistsAsync(newName, excludeId: category.Id, ct)</c>
/// — passing the LOADED category's Id (NOT Guid.Empty) as excludeId.
/// This is what makes a no-op rename ("rename X to X") succeed: the
/// uniqueness check excludes the renamed entity's own row from the
/// candidate-collision set.
/// </summary>
public class RenameCategoryCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static RenameCategoryCommand BuildValidCommand(
        Guid? categoryId = null,
        string? newName = null)
        => new(
            categoryId ?? TestValues.CategoryId,
            newName ?? "Renamed Category");

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - categoryRepository.GetByIdAsync returns a real Category built
    //     via Category.Create("Books"). The category's Id is captured so
    //     tests can verify the handler passes it as excludeId.
    //   - categoryRepository.NameExistsAsync returns false (no collision)
    //   - unitOfWork.SaveChangesAsync returns 1
    private static (
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepo,
        IUnitOfWork unitOfWork,
        ILogger<RenameCategoryCommandHandler> logger,
        Category category)
        BuildMocks(Category? category = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a REAL Category so we can observe the Rename side-effect
        // on the Name property.
        var actualCategory = category ?? Category.Create("Books");

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualCategory);
        // Default: no name collision — a rename to a fresh name succeeds.
        categoryRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(false);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<RenameCategoryCommandHandler>>();

        return (currentUser, categoryRepo, unitOfWork, logger, actualCategory);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNameDoesNotCollide_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        var result = await RenameCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenNameDoesNotCollide_RenamesCategory()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, category) = BuildMocks();
        var command = BuildValidCommand(newName: "Renamed Books");

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        category.Name.Should().Be("Renamed Books");
    }

    [Fact]
    public async Task HandleAsync_WhenNameDoesNotCollide_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── excludeId contract (NOT Guid.Empty) ─────────────────────────────

    // The handler MUST pass the loaded category's Id as excludeId when
    // calling NameExistsAsync — otherwise a no-op rename (renaming X to
    // X) would falsely collide with the renamed entity's own row.
    // We assert the excludeId received by the mock equals the category's
    // loaded Id (NOT Guid.Empty, NOT a different Guid).
    [Fact]
    public async Task HandleAsync_WhenCheckingNameCollision_PassesLoadedCategoryIdAsExcludeId()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, category) = BuildMocks();
        // Capture the excludeId received by the mock.
        Guid? capturedExcludeId = null;
        categoryRepo.NameExistsAsync(
            Arg.Any<string>(),
            Arg.Do<Guid?>(id => capturedExcludeId = id),
            Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        capturedExcludeId.Should().NotBeNull();
        capturedExcludeId.Should().Be(category.Id);
        capturedExcludeId.Should().NotBe(Guid.Empty);
    }

    // ── Name collision rejection ───────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNameAlreadyExists_ReturnsAlreadyExistsFailure()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        // Override the mock — another category already has the new name.
        categoryRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(true);
        var command = BuildValidCommand(newName: "Existing Category");

        // Act
        var result = await RenameCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        // The exact text from the SUT — including the interpolated
        // {command.NewName}.
        result.Error.Should().Be(
            $"Another category with the name 'Existing Category' already exists. Choose a different name.");
    }

    // The collision-rejection path MUST short-circuit BEFORE
    // SaveChanges is called — no partial state should be persisted when
    // the rename is rejected.
    [Fact]
    public async Task HandleAsync_WhenNameAlreadyExists_DoesNotCallSaveChangesAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        categoryRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(true);

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // The collision-rejection path MUST NOT mutate the aggregate's Name
    // (the domain's Rename is only called after the uniqueness check
    // passes). This protects the aggregate from being left in a half-
    // mutated state on rejection.
    [Fact]
    public async Task HandleAsync_WhenNameAlreadyExists_DoesNotMutateCategoryName()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, category) = BuildMocks();
        categoryRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(true);
        var originalName = category.Name;

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        category.Name.Should().Be(originalName);
    }

    // ── Auth rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        // Auth rejection must short-circuit BEFORE any repo / UoW call.
        await categoryRepo.DidNotReceive().GetByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await categoryRepo.DidNotReceive().NameExistsAsync(
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
    }

    // ── Not found ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCategoryNotFound_ReturnsFailureWithCategoryId()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        categoryRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Category?)null);

        // Act
        var result = await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Category '{TestValues.CategoryId}' was not found.");
        // Not-found must short-circuit BEFORE NameExistsAsync.
        await categoryRepo.DidNotReceive().NameExistsAsync(
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    // ── Logger invocations ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_LogsInformation()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task HandleAsync_WhenCategoryNotFound_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        categoryRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Category?)null);

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task HandleAsync_WhenNameAlreadyExists_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        categoryRepo.NameExistsAsync(default!, default, default)
            .ReturnsForAnyArgs(true);

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
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
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await categoryRepo.Received(1).GetByIdAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToNameExistsAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger, ct);

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
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await RenameCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

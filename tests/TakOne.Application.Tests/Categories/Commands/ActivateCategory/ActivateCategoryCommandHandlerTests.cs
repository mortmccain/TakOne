using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Categories.Commands.ActivateCategory;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.ActivateCategory;

/// <summary>
/// Unit tests for <see cref="ActivateCategoryCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the category repository, the
/// unit of work, a logger, and a cancellation token. We mock every
/// collaborator with NSubstitute. The repository returns a REAL
/// <see cref="Category"/> instance (built via <see cref="Category.Create"/>)
/// so we can observe the side-effect of <c>Activate()</c> on
/// <c>IsActive</c>.
///
/// SPECIAL CASE: the handler calls <c>unitOfWork.ClearChangeTracker()</c>
/// BEFORE loading the category. This is the Blazor Server scoped-
/// DbContext stale-tracking bug workaround. We verify it's called.
///
/// NOTE: Activate is idempotent — it does NOT cascade to SubCategories.
/// We verify this by leaving the SubCategories deactivated and asserting
/// they stay deactivated after Activate.
/// </summary>
public class ActivateCategoryCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — the stable TestValues.CategoryId makes
    // failure diffs readable.
    private static ActivateCategoryCommand BuildValidCommand(Guid? categoryId = null)
        => new(categoryId ?? TestValues.CategoryId);

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - categoryRepository.GetByIdAsync returns a real Category built
    //     via Category.Create("Books"). The category is ACTIVE by default.
    //   - unitOfWork.SaveChangesAsync returns 1
    // Each test receives the tuple and can override individual mock calls
    // to exercise a specific rejection path.
    private static (
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepo,
        IUnitOfWork unitOfWork,
        ILogger<ActivateCategoryCommandHandler> logger,
        Category category)
        BuildMocks(Category? category = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a REAL Category instance so we can observe the side-effect
        // of Activate() on IsActive. Category.Create sets IsActive=true
        // by default; tests that exercise reactivation can pass a
        // pre-deactivated instance.
        var actualCategory = category ?? Category.Create("Books");

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualCategory);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<ActivateCategoryCommandHandler>>();

        return (currentUser, categoryRepo, unitOfWork, logger, actualCategory);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCategoryExists_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        var result = await ActivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    // Activating an already-active Category is a no-op — the domain's
    // Activate() unconditionally sets IsActive=true. We assert the
    // domain method was called (not the persistence) and IsActive is
    // true afterward.
    [Fact]
    public async Task HandleAsync_WhenCategoryIsAlreadyActive_StaysActive()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, category) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        await ActivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        category.IsActive.Should().BeTrue();
    }

    // Activating a previously-deactivated Category flips IsActive to true.
    [Fact]
    public async Task HandleAsync_WhenCategoryIsDeactivated_ReactivatesIt()
    {
        // Arrange
        // Build a Category, deactivate it, then wire it into the mock.
        var category = Category.Create("Books");
        category.Deactivate();
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks(category);
        var command = BuildValidCommand();

        // Act
        await ActivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        category.IsActive.Should().BeTrue();
    }

    // Activate is idempotent AND does NOT cascade to SubCategories.
    // If a parent is deactivated along with its SubCategories, activating
    // the parent reactivates the parent only — the children stay
    // deactivated (per the command XML doc's "NOTE ON CASCADE").
    [Fact]
    public async Task HandleAsync_WhenCategoryHasDeactivatedSubCategories_DoesNotReactivateSubCategories()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Novels");
        // Cascade-deactivate: parent + child both go inactive.
        category.Deactivate();
        // Sanity check: both ARE deactivated before the handler runs.
        category.IsActive.Should().BeFalse();
        sub.IsActive.Should().BeFalse();

        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks(category);
        var command = BuildValidCommand();

        // Act
        await ActivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // Parent is reactivated, child is NOT (no cascade).
        category.IsActive.Should().BeTrue();
        sub.IsActive.Should().BeFalse();
    }

    // ── ClearChangeTracker (Blazor Server stale-tracking workaround) ───

    // The handler calls ClearChangeTracker BEFORE loading the Category to
    // prevent the Blazor Server scoped-DbContext stale-tracking bug.
    // We assert the call was made once.
    [Fact]
    public async Task HandleAsync_WhenCategoryExists_CallsClearChangeTrackerOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        await ActivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        unitOfWork.Received(1).ClearChangeTracker();
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await ActivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Auth rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await ActivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        // Auth rejection must short-circuit BEFORE any repo / UoW call.
        await categoryRepo.DidNotReceive().GetByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        unitOfWork.DidNotReceive().ClearChangeTracker();
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        // IsAuthenticated=true but UserId=Guid.Empty is still rejected —
        // the second branch of the auth check catches the missing id.
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await ActivateCategoryCommandHandler.HandleAsync(
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
        // Override the mock to return null — simulates a missing category
        // row or a wrong id passed in by the caller.
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        categoryRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Category?)null);

        // Act
        var result = await ActivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Category '{TestValues.CategoryId}' was not found.");
        // Not-found must short-circuit BEFORE SaveChanges is called.
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Logger invocations ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await ActivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // NSubstitute can't directly match ILogger extension methods
        // (LogInformation/LogWarning) because they wrap the underlying
        // Log<TState> call with a different argument shape. We use the
        // underlying Log method with Arg.AnyType to match the generic
        // state parameter.
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
        await ActivateCategoryCommandHandler.HandleAsync(
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
        await ActivateCategoryCommandHandler.HandleAsync(
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
        await ActivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await categoryRepo.Received(1).GetByIdAsync(
            Arg.Any<Guid>(),
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
        await ActivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

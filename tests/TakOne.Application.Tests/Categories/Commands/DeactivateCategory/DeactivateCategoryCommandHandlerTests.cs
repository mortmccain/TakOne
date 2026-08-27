using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Categories.Commands.DeactivateCategory;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.DeactivateCategory;

/// <summary>
/// Unit tests for <see cref="DeactivateCategoryCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the category repository, the
/// unit of work, a logger, and a cancellation token. We mock every
/// collaborator with NSubstitute. The repository returns a REAL
/// <see cref="Category"/> instance (built via <see cref="Category.Create"/>
/// + <see cref="Category.AddSubCategory"/> + <see cref="SubCategory.AddSubSubCategory"/>)
/// so we can observe the cascade side-effect of <c>Deactivate()</c>
/// on the parent + its SubCategories + SubSubCategories.
///
/// SPECIAL CASE: the handler uses
/// <c>categoryRepository.GetByIdWithHierarchyAsync(categoryId, ct)</c>
/// (NOT the lighter <c>GetByIdAsync</c>) because Deactivate cascades to
/// SubCategories + SubSubCategories and EF Core must have them tracked.
/// We verify the right repo method is called.
/// </summary>
public class DeactivateCategoryCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static DeactivateCategoryCommand BuildValidCommand(Guid? categoryId = null)
        => new(categoryId ?? TestValues.CategoryId);

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - categoryRepository.GetByIdWithHierarchyAsync returns a real
    //     Category with a 2-level hierarchy (Sub + SubSub) so cascade
    //     tests can observe the propagation.
    //   - unitOfWork.SaveChangesAsync returns 1
    //
    // NOTE: when the caller passes in a pre-built category (e.g. a
    // pre-deactivated one), we DON'T add a fresh Sub+SubSub — the
    // category is already in its terminal state and AddSubCategory
    // would throw (the aggregate's EnsureActive guard fires).
    // Instead, we look up the category's EXISTING first sub + first
    // subsub (if any) so the test can still assert on cascade state.
    private static (
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepo,
        IUnitOfWork unitOfWork,
        ILogger<DeactivateCategoryCommandHandler> logger,
        Category category,
        SubCategory? subCategory,
        SubSubCategory? subSubCategory)
        BuildMocks(Category? category = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        SubCategory? sub = null;
        SubSubCategory? subSub = null;
        Category actualCategory;
        if (category is null)
        {
            // Default path: build a fresh Category with a 2-level
            // hierarchy so cascade side-effects are observable. We use
            // the PUBLIC AddSubCategory + AddSubSubCategory methods
            // (the SubCategory / SubSubCategory ctors are internal —
            // only the Category aggregate can construct them).
            actualCategory = Category.Create("Books");
            sub = actualCategory.AddSubCategory("Novels");
            subSub = actualCategory.AddSubSubCategory(sub.Id, "Sci-Fi");
        }
        else
        {
            // Caller-provided category — use as-is. Look up any existing
            // Sub + SubSub so the test can assert on cascade state.
            actualCategory = category;
            sub = actualCategory.SubCategories.FirstOrDefault();
            if (sub is not null)
            {
                subSub = sub.SubSubCategories.FirstOrDefault();
            }
        }

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.GetByIdWithHierarchyAsync(default, default)
            .ReturnsForAnyArgs(actualCategory);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<DeactivateCategoryCommandHandler>>();

        return (currentUser, categoryRepo, unitOfWork, logger, actualCategory, sub, subSub);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCategoryExists_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        var result = await DeactivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenCategoryIsAlreadyActive_DeactivatesIt()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, category, _, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        category.IsActive.Should().BeFalse();
    }

    // Deactivate cascades to SubCategories AND SubSubCategories (per the
    // command XML doc's CASCADE section). This is what distinguishes
    // Deactivate from Activate (which is non-cascading). The handler
    // delegates to category.Deactivate() which does the cascade at the
    // domain level.
    [Fact]
    public async Task HandleAsync_WhenCategoryHasSubAndSubSub_CascadesDeactivationToAll()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, category, sub, subSub) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // All three levels should now be deactivated.
        category.IsActive.Should().BeFalse();
        sub.IsActive.Should().BeFalse();
        subSub.IsActive.Should().BeFalse();
    }

    // Deactivating an already-deactivated Category is a no-op — the
    // domain method unconditionally sets IsActive=false. SaveChanges is
    // still called (the SUT's comment documents this is by design —
    // EF Core will just detect no changes and the round-trip is a null-op).
    [Fact]
    public async Task HandleAsync_WhenCategoryIsAlreadyDeactivated_StillCallsSaveChanges()
    {
        // Arrange
        var category = Category.Create("Books");
        category.Deactivate();
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks(category);
        var command = BuildValidCommand();

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Hierarchy loading (NOT the lightweight GetByIdAsync) ──────────

    // Deactivate needs the full hierarchy because the cascade mutates
    // SubCategories + SubSubCategories — EF Core must have them tracked
    // to generate UPDATE statements for them. The handler MUST call
    // GetByIdWithHierarchyAsync, NOT the lightweight GetByIdAsync.
    [Fact]
    public async Task HandleAsync_WhenCategoryExists_CallsGetByIdWithHierarchyAsyncNotGetByIdAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await categoryRepo.Received(1).GetByIdWithHierarchyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        // The lightweight GetByIdAsync is NOT used — Deactivate needs
        // the full hierarchy for the cascade.
        await categoryRepo.DidNotReceive().GetByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── ClearChangeTracker (Blazor Server stale-tracking workaround) ───

    [Fact]
    public async Task HandleAsync_WhenCategoryExists_CallsClearChangeTrackerOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        unitOfWork.Received(1).ClearChangeTracker();
    }

    // ── Auth rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await DeactivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        await categoryRepo.DidNotReceive().GetByIdWithHierarchyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        unitOfWork.DidNotReceive().ClearChangeTracker();
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await DeactivateCategoryCommandHandler.HandleAsync(
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        categoryRepo.GetByIdWithHierarchyAsync(default, default)
            .ReturnsForAnyArgs((Category?)null);

        // Act
        var result = await DeactivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Category '{TestValues.CategoryId}' was not found.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Logger invocations ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        categoryRepo.GetByIdWithHierarchyAsync(default, default)
            .ReturnsForAnyArgs((Category?)null);

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
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
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToGetByIdWithHierarchyAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await categoryRepo.Received(1).GetByIdWithHierarchyAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToSaveChangesAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await DeactivateCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

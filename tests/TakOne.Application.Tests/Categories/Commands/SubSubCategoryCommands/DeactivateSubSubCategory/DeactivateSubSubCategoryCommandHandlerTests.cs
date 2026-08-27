using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Categories.Commands.SubSubCategoryCommands.DeactivateSubSubCategory;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.SubSubCategoryCommands.DeactivateSubSubCategory;

/// <summary>
/// Unit tests for <see cref="DeactivateSubSubCategoryCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the category repository, the
/// unit of work, a logger, and a cancellation token. We mock every
/// collaborator with NSubstitute. The repository returns a REAL
/// <see cref="Category"/> instance (built via <see cref="Category.Create"/>
/// + <see cref="Category.AddSubCategory"/> + <see cref="Category.AddSubSubCategory"/>)
/// so we can observe the side-effect of <c>DeactivateSubSubCategory</c>
/// on the SubSubCategory's <c>IsActive</c> flag.
///
/// SPECIAL CASE: the handler try/catches DomainException around
/// <c>category.DeactivateSubSubCategory(...)</c> — the exception
/// message becomes the Result.Failure error. (Deactivate is idempotent
/// per the command XML doc — no DomainException is thrown on already-
/// deactivated entities.)
/// </summary>
public class DeactivateSubSubCategoryCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static DeactivateSubSubCategoryCommand BuildValidCommand(
        Guid categoryId,
        Guid subCategoryId,
        Guid subSubCategoryId)
        => new(categoryId, subCategoryId, subSubCategoryId);

    // Mirrors the Activate test helper — builds a real Category with a
    // 2-level hierarchy so we can observe the side-effect of
    // DeactivateSubSubCategory on the SubSub's IsActive flag.
    //
    // NOTE: when the caller passes in a pre-built category, we DON'T
    // add a fresh Sub+SubSub — the category may already be in a state
    // where AddSubCategory/AddSubSubCategory would throw. We look up
    // the category's EXISTING first sub + first subsub so the test can
    // still assert on cascade state.
    private static (
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepo,
        IUnitOfWork unitOfWork,
        ILogger<DeactivateSubSubCategoryCommandHandler> logger,
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
            actualCategory = Category.Create("Books");
            sub = actualCategory.AddSubCategory("Novels");
            subSub = actualCategory.AddSubSubCategory(sub.Id, "Sci-Fi");
        }
        else
        {
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

        var logger = Substitute.For<ILogger<DeactivateSubSubCategoryCommandHandler>>();

        return (currentUser, categoryRepo, unitOfWork, logger, actualCategory, sub, subSub);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenSubSubCategoryExists_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        var result = await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenSubSubCategoryIsAlreadyActive_DeactivatesIt()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        subSub.IsActive.Should().BeFalse();
    }

    // Deactivating an already-deactivated SubSubCategory is a no-op —
    // the domain's Deactivate method unconditionally sets IsActive=false.
    // SaveChanges is still called (the SUT's comment documents this is
    // by design — idempotent no-op → EF Core round-trip is null-op).
    [Fact]
    public async Task HandleAsync_WhenSubSubCategoryIsAlreadyDeactivated_StillCallsSaveChanges()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Novels");
        var subSub = category.AddSubSubCategory(sub.Id, "Sci-Fi");
        category.DeactivateSubSubCategory(sub.Id, subSub.Id);

        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks(category);
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        subSub.IsActive.Should().BeFalse();
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── ClearChangeTracker + hierarchy loading ───────────────────────

    [Fact]
    public async Task HandleAsync_WhenCategoryExists_CallsClearChangeTrackerOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        var result = await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        var result = await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        categoryRepo.GetByIdWithHierarchyAsync(default, default)
            .ReturnsForAnyArgs((Category?)null);
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        var result = await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Category '{TestValues.CategoryId}' was not found.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── DomainException propagation ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenSubSubCategoryIdNotUnderSubCategory_ReturnsFailureWithDomainExceptionMessage()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, _) = BuildMocks();
        var unknownSubSubId = Guid.NewGuid();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, unknownSubSubId);

        // Act
        var result = await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(
            $"SubSubCategory with Id '{unknownSubSubId}' was not found under SubCategory 'Novels'.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Logger invocations ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        categoryRepo.GetByIdWithHierarchyAsync(default, default)
            .ReturnsForAnyArgs((Category?)null);
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
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
    public async Task HandleAsync_WhenAggregateRejectsDeactivation_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, _) = BuildMocks();
        var unknownSubSubId = Guid.NewGuid();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, unknownSubSubId);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await categoryRepo.Received(1).GetByIdWithHierarchyAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToSaveChangesAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id);

        // Act
        await DeactivateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

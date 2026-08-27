using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Categories.Commands.SubSubCategoryCommands.RenameSubSubCategory;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.SubSubCategoryCommands.RenameSubSubCategory;

/// <summary>
/// Unit tests for <see cref="RenameSubSubCategoryCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the category repository, the
/// unit of work, a logger, and a cancellation token. We mock every
/// collaborator with NSubstitute. The repository returns a REAL
/// <see cref="Category"/> instance (built via <see cref="Category.Create"/>
/// + <see cref="Category.AddSubCategory"/> + <see cref="Category.AddSubSubCategory"/>)
/// so we can observe the side-effect of <c>RenameSubSubCategory</c>
/// on the SubSubCategory's <c>Name</c> property.
///
/// SPECIAL CASE: the handler try/catches DomainException around
/// <c>category.RenameSubSubCategory(...)</c>. The aggregate's
/// RenameSubSubCategory throws on:
///   - parent Category deactivated
///   - SubCategoryId does not exist
///   - SubCategory deactivated
///   - SubSubCategoryId does not exist under the SubCategory
///   - sibling name collision (case-insensitive, excluding the renamed
///     entity's own Id — so renaming to the same name is a no-op)
/// </summary>
public class RenameSubSubCategoryCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static RenameSubSubCategoryCommand BuildValidCommand(
        Guid categoryId,
        Guid subCategoryId,
        Guid subSubCategoryId,
        string newName)
        => new(categoryId, subCategoryId, subSubCategoryId, newName);

    // Mirrors the Activate/Deactivate test helper — builds a real
    // Category with a 2-level hierarchy.
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
        ILogger<RenameSubSubCategoryCommandHandler> logger,
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

        var logger = Substitute.For<ILogger<RenameSubSubCategoryCommandHandler>>();

        return (currentUser, categoryRepo, unitOfWork, logger, actualCategory, sub, subSub);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNameDoesNotCollide_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        var result = await RenameSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenNameDoesNotCollide_RenamesSubSubCategory()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        await RenameSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        subSub.Name.Should().Be("Fantasy");
    }

    // The aggregate excludes the renamed entity's own Id from the
    // sibling-collision check — so renaming to the SAME name (a no-op
    // rename) is allowed and succeeds. This protects admin workflows
    // that rename to "fix capitalization" or similar cosmetic edits.
    [Fact]
    public async Task HandleAsync_WhenRenamingToSameName_SucceedsNoOp()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        // Rename "Sci-Fi" to "Sci-Fi" — the aggregate's collision check
        // excludes the renamed entity's own Id, so this is a no-op.
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Sci-Fi");

        // Act
        var result = await RenameSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        subSub.Name.Should().Be("Sci-Fi");
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        await RenameSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── ClearChangeTracker ────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCategoryExists_CallsClearChangeTrackerOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        await RenameSubSubCategoryCommandHandler.HandleAsync(
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
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        var result = await RenameSubSubCategoryCommandHandler.HandleAsync(
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
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        var result = await RenameSubSubCategoryCommandHandler.HandleAsync(
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
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        var result = await RenameSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Category '{TestValues.CategoryId}' was not found.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── DomainException propagation ────────────────────────────────────

    // The aggregate's EnsureSubSubCategoryExists throws DomainException
    // when the SubSubCategoryId does not exist under the named SubCategory.
    // We verify the handler catches the exception and forwards its
    // message verbatim as Result.Failure(ex.Message).
    [Fact]
    public async Task HandleAsync_WhenSubSubCategoryIdNotUnderSubCategory_ReturnsFailureWithDomainExceptionMessage()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, _) = BuildMocks();
        var unknownSubSubId = Guid.NewGuid();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, unknownSubSubId, "Fantasy");

        // Act
        var result = await RenameSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(
            $"SubSubCategory with Id '{unknownSubSubId}' was not found under SubCategory 'Novels'.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // Sibling name collision: pre-seed an existing sibling with the name
    // "Fantasy"; then ask the handler to rename a different SubSubCategory
    // to "Fantasy". The aggregate's EnsureSubSubCategoryNameUnique throws.
    [Fact]
    public async Task HandleAsync_WhenSiblingNameCollision_ReturnsFailureWithCollisionMessage()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Novels");
        // Pre-seed an existing sibling named "Fantasy".
        category.AddSubSubCategory(sub.Id, "Fantasy");
        // Then add the SubSub that we'll attempt to rename.
        var subSub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        var (currentUser, categoryRepo, unitOfWork, logger, _, _, _) = BuildMocks(category);
        // Rename "Sci-Fi" → "Fantasy" — the collision check throws.
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        var result = await RenameSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"A SubSubCategory named 'Fantasy' already exists under SubCategory 'Novels'.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Logger invocations ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, subSub) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        await RenameSubSubCategoryCommandHandler.HandleAsync(
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
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        await RenameSubSubCategoryCommandHandler.HandleAsync(
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
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        await RenameSubSubCategoryCommandHandler.HandleAsync(
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
    public async Task HandleAsync_WhenAggregateRejectsRename_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub, _) = BuildMocks();
        var unknownSubSubId = Guid.NewGuid();
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, unknownSubSubId, "Fantasy");

        // Act
        await RenameSubSubCategoryCommandHandler.HandleAsync(
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
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        await RenameSubSubCategoryCommandHandler.HandleAsync(
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
        var command = BuildValidCommand(TestValues.CategoryId, sub.Id, subSub.Id, "Fantasy");

        // Act
        await RenameSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

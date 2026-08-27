using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Categories.Commands.SubSubCategoryCommands.CreateSubSubCategory;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.SubSubCategoryCommands.CreateSubSubCategory;

/// <summary>
/// Unit tests for <see cref="CreateSubSubCategoryCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the category repository, the
/// unit of work, a logger, and a cancellation token. We mock every
/// collaborator with NSubstitute. The repository returns a REAL
/// <see cref="Category"/> instance (built via <see cref="Category.Create"/>
/// + <see cref="Category.AddSubCategory"/>) so we can observe the side-
/// effect of <c>AddSubSubCategory</c> on the SubCategory's
/// SubSubCategories collection.
///
/// SPECIAL CASES:
///   1. The handler uses
///      <c>categoryRepository.GetByIdWithHierarchyNoTrackingAsync(categoryId, ct)</c>
///      (NOT the tracked <c>GetByIdWithHierarchyAsync</c>) — AsNoTracking
///      to dodge the Blazor Server stale-tracking bug. After the
///      aggregate returns the new SubSubCategory, the handler explicitly
///      tracks ONLY the new entity via <c>unitOfWork.AddEntity(subSub)</c>.
///   2. The handler try/catches DomainException around
///      <c>category.AddSubSubCategory(...)</c> — the exception message
///      becomes the Result.Failure error (so the UI can show a
///      friendly error). We verify the message is forwarded verbatim.
/// </summary>
public class CreateSubSubCategoryCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static CreateSubSubCategoryCommand BuildValidCommand(
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        string? name = null)
        => new(
            categoryId ?? TestValues.CategoryId,
            subCategoryId ?? TestValues.SubCategoryId,
            name ?? "New SubSub");

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - categoryRepository.GetByIdWithHierarchyNoTrackingAsync
    //     returns a real Category with a 1-level SubCategory hierarchy
    //     (so AddSubSubCategory succeeds). The SubCategory's Id is the
    //     stable TestValues.SubCategoryId so the test's command can
    //     reference it.
    //   - unitOfWork.SaveChangesAsync returns 1
    // Each test receives the tuple and can override individual mock calls
    // to exercise a specific rejection path.
    private static (
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepo,
        IUnitOfWork unitOfWork,
        ILogger<CreateSubSubCategoryCommandHandler> logger,
        Category category,
        SubCategory subCategory)
        BuildMocks(Category? category = null, SubCategory? subCategory = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Build a real Category + SubCategory so AddSubSubCategory can
        // succeed. The SubCategory is added via the Category aggregate's
        // public AddSubCategory method — but its Id is random. We need
        // the test command to pass a known SubCategoryId, so we use the
        // sub's real Id in the test command builder (each test does this
        // explicitly when it constructs the command).
        var actualCategory = category ?? Category.Create("Books");
        var actualSub = subCategory ?? actualCategory.AddSubCategory("Novels");

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.GetByIdWithHierarchyNoTrackingAsync(default, default)
            .ReturnsForAnyArgs(actualCategory);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<CreateSubSubCategoryCommandHandler>>();

        return (currentUser, categoryRepo, unitOfWork, logger, actualCategory, actualSub);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_ReturnsSuccessWithNewSubSubCategoryId()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        // Use the sub's real Id so AddSubSubCategory finds it.
        var command = BuildValidCommand(subCategoryId: sub.Id, name: "Sci-Fi");

        // Act
        var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_AddsSubSubToSubCategoryInMemory()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        var command = BuildValidCommand(subCategoryId: sub.Id, name: "Sci-Fi");

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // The aggregate appended the new SubSubCategory to its SubCategory's
        // in-memory collection. (The AsNoTracking load means this has no DB
        // effect — the parent is untracked — but the collection mutation
        // is still observable on the in-memory instance.)
        sub.SubSubCategories.Should().ContainSingle(s => s.Name == "Sci-Fi");
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        var command = BuildValidCommand(subCategoryId: sub.Id);

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── NoTracking load + AddEntity ───────────────────────────────────

    // The handler MUST use GetByIdWithHierarchyNoTrackingAsync (NOT the
    // tracked GetByIdWithHierarchyAsync) — the AsNoTracking version
    // dodges the Blazor Server stale-tracking bug (see the SUT's
    // extensive XML doc on why the tracked version doesn't work).
    [Fact]
    public async Task HandleAsync_WhenCategoryExists_CallsGetByIdWithHierarchyNoTrackingAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        var command = BuildValidCommand(subCategoryId: sub.Id);

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await categoryRepo.Received(1).GetByIdWithHierarchyNoTrackingAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        // The TRACKED version must NOT be called — that's the bug.
        await categoryRepo.DidNotReceive().GetByIdWithHierarchyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // After AddSubSubCategory returns the new SubSubCategory, the handler
    // explicitly tracks ONLY the new entity via unitOfWork.AddEntity(subSub).
    // This is the reliable pattern that avoids the stale-tracking bug —
    // the parent + siblings stay untracked, SaveChanges generates exactly
    // ONE INSERT and zero UPDATEs.
    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsAddEntityWithNewSubSubCategory()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        var command = BuildValidCommand(subCategoryId: sub.Id, name: "Sci-Fi");

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        unitOfWork.Received(1).AddEntity(Arg.Any<SubSubCategory>());
    }

    // ── Auth rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
            BuildValidCommand(subCategoryId: sub.Id), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        await categoryRepo.DidNotReceive().GetByIdWithHierarchyNoTrackingAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        unitOfWork.DidNotReceive().AddEntity(Arg.Any<object>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
            BuildValidCommand(subCategoryId: sub.Id), currentUser, categoryRepo, unitOfWork, logger,
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, _) = BuildMocks();
        categoryRepo.GetByIdWithHierarchyNoTrackingAsync(default, default)
            .ReturnsForAnyArgs((Category?)null);

        // Act
        var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Category '{TestValues.CategoryId}' was not found.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        unitOfWork.DidNotReceive().AddEntity(Arg.Any<object>());
    }

    // ── DomainException propagation ────────────────────────────────────

    // The aggregate throws DomainException when:
    //   - the parent Category is deactivated
    //   - the SubCategoryId does not exist under this Category
    //   - the SubCategory is deactivated
    //   - a sibling SubSubCategory already has the new name
    // The handler catches DomainException and converts it to
    // Result<Guid>.Failure(ex.Message). We verify the exception message
    // is forwarded verbatim (so the UI's localized error display can
    // rely on the exact text).

    [Fact]
    public async Task HandleAsync_WhenSubCategoryIdNotUnderCategory_ReturnsFailureWithDomainExceptionMessage()
    {
        // Arrange
        // Pass a SubCategoryId that does NOT exist under the loaded
        // Category. The aggregate's EnsureSubCategoryExists throws
        // DomainException with the exact message:
        //   "SubCategory with Id '{subCategoryId}' was not found under Category '{Name}'."
        var (currentUser, categoryRepo, unitOfWork, logger, _, _) = BuildMocks();
        var unknownSubId = Guid.NewGuid();
        var command = BuildValidCommand(subCategoryId: unknownSubId);

        // Act
        var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(
            $"SubCategory with Id '{unknownSubId}' was not found under Category 'Books'.");
        // DomainException short-circuits BEFORE AddEntity + SaveChanges.
        unitOfWork.DidNotReceive().AddEntity(Arg.Any<object>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenSiblingSubSubCategoryAlreadyExists_ReturnsFailureWithCollisionMessage()
    {
        // Arrange
        // First add a SubSubCategory via the aggregate; then ask the
        // handler to create a SECOND SubSubCategory with the SAME name.
        // The aggregate's EnsureSubSubCategoryNameUnique throws
        // DomainException with the collision message.
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Novels");
        // Pre-seed an existing sibling with the name "Sci-Fi".
        category.AddSubSubCategory(sub.Id, "Sci-Fi");

        var (currentUser, categoryRepo, unitOfWork, logger, _, _) = BuildMocks(category, sub);
        // Now try to add another SubSubCategory with the same name.
        var command = BuildValidCommand(subCategoryId: sub.Id, name: "Sci-Fi");

        // Act
        var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"A SubSubCategory named 'Sci-Fi' already exists under SubCategory 'Novels'.");
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenParentCategoryIsDeactivated_ReturnsFailureWithCategoryDeactivatedMessage()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Novels");
        // Deactivate the parent Category — AddSubSubCategory will throw
        // DomainException because the aggregate's EnsureActive check fails.
        category.Deactivate();

        var (currentUser, categoryRepo, unitOfWork, logger, _, _) = BuildMocks(category, sub);
        var command = BuildValidCommand(subCategoryId: sub.Id, name: "Sci-Fi");

        // Act
        var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Cannot modify Category 'Books' because it is deactivated.");
    }

    [Fact]
    public async Task HandleAsync_WhenSubCategoryIsDeactivated_ReturnsFailureWithSubCategoryDeactivatedMessage()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Novels");
        // Deactivate the parent SubCategory via the Category aggregate
        // (cascade-deactivates the sub). The Category itself stays active.
        category.DeactivateSubCategory(sub.Id);
        category.Activate(); // Re-activate the parent Category — but sub stays deactivated.

        var (currentUser, categoryRepo, unitOfWork, logger, _, _) = BuildMocks(category, sub);
        var command = BuildValidCommand(subCategoryId: sub.Id, name: "Sci-Fi");

        // Act
        var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
            command, currentUser, categoryRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Cannot modify SubCategory 'Novels' because it is deactivated.");
    }

    // ── Logger invocations ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
            BuildValidCommand(subCategoryId: sub.Id), currentUser, categoryRepo, unitOfWork, logger,
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        var command = BuildValidCommand(subCategoryId: sub.Id);

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
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
        var (currentUser, categoryRepo, unitOfWork, logger, _, _) = BuildMocks();
        categoryRepo.GetByIdWithHierarchyNoTrackingAsync(default, default)
            .ReturnsForAnyArgs((Category?)null);

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
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
    public async Task HandleAsync_WhenAggregateRejectsCreation_LogsWarning()
    {
        // Arrange
        // The aggregate's EnsureActive throws when the parent Category
        // is deactivated. The handler catches DomainException and logs
        // a warning before returning Result.Failure.
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Novels");
        category.Deactivate();

        var (currentUser, categoryRepo, unitOfWork, logger, _, _) = BuildMocks(category, sub);
        var command = BuildValidCommand(subCategoryId: sub.Id, name: "Sci-Fi");

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
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
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToGetByIdWithHierarchyNoTrackingAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
            BuildValidCommand(subCategoryId: sub.Id), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await categoryRepo.Received(1).GetByIdWithHierarchyNoTrackingAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToSaveChangesAsync()
    {
        // Arrange
        var (currentUser, categoryRepo, unitOfWork, logger, _, sub) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await CreateSubSubCategoryCommandHandler.HandleAsync(
            BuildValidCommand(subCategoryId: sub.Id), currentUser, categoryRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

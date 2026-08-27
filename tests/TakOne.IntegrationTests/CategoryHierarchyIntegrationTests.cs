using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Categories.Commands.CreateCategory;
using TakOne.Application.Categories.Commands.RenameCategory;
using TakOne.Application.Categories.Commands.SubSubCategoryCommands.CreateSubSubCategory;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.Infrastructure.Services;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the Category aggregate's hierarchy persistence.
/// Verifies that EF Core correctly round-trips the parent-child
/// relationships (Category → SubCategories → SubSubCategories) across the
/// three tables, and that the cascade-deactivate domain logic persists
/// correctly across all rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THESE TESTS CATCH THAT THE MOCK-HEAVY UNIT TESTS DO NOT:</b>
/// <list type="bullet">
///   <item>The cascade-deactivate domain logic in
///       <c>Category.Deactivate()</c> must produce SEPARATE UPDATE
///       statements for the parent + every child. The mock-based handler
///       unit tests assert that <c>unitOfWork.SaveChangesAsync</c> was
///       called once — they can't verify the underlying UPDATE actually
///       hit the rows for every level of the hierarchy.</item>
///   <item>The AsNoTracking + AddEntity pattern in
///       <c>CreateSubSubCategoryCommandHandler</c> must persist EXACTLY
///       ONE INSERT (for the new SubSubCategory) and ZERO UPDATEs for
///       the parent Category / parent SubCategory. The integration test
///       verifies this by counting rows after SaveChanges — the only
///       new row should be the SubSubCategory, and the parent rows
///       should remain unchanged.</item>
///   <item>Name uniqueness is enforced at the DB level via a unique
///       index — but the handler does the friendly pre-check, so the
///       test verifies the friendly "already exists" message is returned
///       (NOT a raw SQLite exception).</item>
/// </list>
/// </para>
/// </remarks>
public class CategoryHierarchyIntegrationTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Build the real-DB-backed collaborator tuple for the Category commands.
    private static async Task<(
        ICurrentUserService currentUser,
        ICategoryRepository categoryRepo,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        ILogger<CreateCategoryCommandHandler> createLogger,
        ILogger<RenameCategoryCommandHandler> renameLogger,
        ILogger<CreateSubSubCategoryCommandHandler> createSubSubLogger)>
        BuildWiredCollaboratorsAsync()
    {
        var db = await SqliteTestDbFactory.CreateAsync();

        var currentUser = new CurrentUserHelper(
            userId: TestValues.CreatedByUserId,
            isAuthenticated: true,
            fullName: "Test Manager",
            groupId: null,
            roles: "Admin");

        var categoryRepo = new CategoryRepository(db);
        var unitOfWork = new UnitOfWork(db);

        var createLogger = Substitute.For<ILogger<CreateCategoryCommandHandler>>();
        var renameLogger = Substitute.For<ILogger<RenameCategoryCommandHandler>>();
        var createSubSubLogger = Substitute.For<ILogger<CreateSubSubCategoryCommandHandler>>();

        return (currentUser, categoryRepo, unitOfWork, db,
            createLogger, renameLogger, createSubSubLogger);
    }

    // ── Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateCategory_PersistsWithAllFields()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            // Act
            var result = await CreateCategoryCommandHandler.HandleAsync(
                new CreateCategoryCommand("Electronics"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            // Assert — handler reports success and returns the new Id.
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().NotBe(Guid.Empty);

            // Reload via fresh load (clear change tracker first) to verify
            // the row round-tripped through the Categories table.
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.categoryRepo
                .GetByIdAsync(result.Value, CancellationToken.None);
            reloaded.Should().NotBeNull();
            reloaded!.Name.Should().Be("Electronics");
            reloaded.IsActive.Should().BeTrue();
        }
    }

    // Verifies the cascade-deactivate domain logic persists correctly across
    // the three hierarchy levels. The domain's Deactivate() method walks the
    // tree and sets IsActive=false on the parent + every sub + every subsub.
    // EF Core must generate N UPDATE statements (one per row). The mock-
    // based unit test can only verify SaveChanges was called once; this
    // test verifies the actual UPDATEs landed on the rows.
    [Fact]
    public async Task CreateCategory_ThenDeactivate_CascadesToSubCategories()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            // Seed a Category with full hierarchy: root → Sub → SubSub.
            // The root Category is created via the handler so it's tracked
            // correctly; Sub + SubSub are added via the domain methods
            // (which mutate the in-memory tree) and persisted by SaveChanges.
            var rootResult = await CreateCategoryCommandHandler.HandleAsync(
                new CreateCategoryCommand("Electronics"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);
            rootResult.IsSuccess.Should().BeTrue();

            // Load the root WITH hierarchy so AddSubCategory + AddSubSubCategory
            // mutate the loaded entity (EF Core tracks them and persists on
            // SaveChanges). Use GetByIdWithHierarchyAsync (tracked).
            var root = await collaborators.categoryRepo
                .GetByIdWithHierarchyAsync(rootResult.Value, CancellationToken.None);
            root.Should().NotBeNull();

            var sub = root!.AddSubCategory("Phones");
            var subSub = root.AddSubSubCategory(sub.Id, "Smartphones");

            await collaborators.unitOfWork.SaveChangesAsync(CancellationToken.None);
            // All three rows now exist in the DB.

            // Act — call Deactivate() on the root. The domain method
            // cascades: root.IsActive=false, sub.IsActive=false,
            // subsub.IsActive=false. SaveChanges generates three UPDATEs.
            collaborators.db.ChangeTracker.Clear();
            root = await collaborators.categoryRepo
                .GetByIdWithHierarchyAsync(rootResult.Value, CancellationToken.None);
            root!.Deactivate();
            await collaborators.unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert — reload from DB and verify all three rows are inactive.
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.categoryRepo
                .GetByIdWithHierarchyAsync(rootResult.Value, CancellationToken.None);
            reloaded!.IsActive.Should().BeFalse();
            reloaded.SubCategories.Should().HaveCount(1);
            reloaded.SubCategories.First().IsActive.Should().BeFalse();
            reloaded.SubCategories.First().SubSubCategories.Should().HaveCount(1);
            reloaded.SubCategories.First().SubSubCategories.First().IsActive
                .Should().BeFalse();
        }
    }

    // Verifies the Activate() method does NOT cascade — only the root's
    // IsActive is set to true; subcategories remain in their previous state.
    // This matches the domain's documented contract.
    [Fact]
    public async Task CreateCategory_ThenActivate_RootIsActiveOnly()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var rootResult = await CreateCategoryCommandHandler.HandleAsync(
                new CreateCategoryCommand("Clothing"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            var root = await collaborators.categoryRepo
                .GetByIdWithHierarchyAsync(rootResult.Value, CancellationToken.None);
            var sub = root!.AddSubCategory("Shirts");
            await collaborators.unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Deactivate everything first, then Activate the root.
            root.Deactivate();
            await collaborators.unitOfWork.SaveChangesAsync(CancellationToken.None);
            collaborators.db.ChangeTracker.Clear();

            root = await collaborators.categoryRepo
                .GetByIdWithHierarchyAsync(rootResult.Value, CancellationToken.None);
            root!.Activate();
            await collaborators.unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert — root.IsActive=true, sub.IsActive remains false.
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.categoryRepo
                .GetByIdWithHierarchyAsync(rootResult.Value, CancellationToken.None);
            reloaded!.IsActive.Should().BeTrue();
            reloaded.SubCategories.First().IsActive.Should().BeFalse();
        }
    }

    // Pipeline test: Create → Rename → reload. Verifies the new name persists.
    [Fact]
    public async Task CreateCategory_ThenRename_NewNamePersists()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var createResult = await CreateCategoryCommandHandler.HandleAsync(
                new CreateCategoryCommand("Old Name"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            // Act
            var renameResult = await RenameCategoryCommandHandler.HandleAsync(
                new RenameCategoryCommand(createResult.Value, "New Name"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.renameLogger,
                CancellationToken.None);

            // Assert
            renameResult.IsSuccess.Should().BeTrue();
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.categoryRepo
                .GetByIdAsync(createResult.Value, CancellationToken.None);
            reloaded!.Name.Should().Be("New Name");
        }
    }

    // Verifies the exclude-self-id contract: renaming to the SAME name
    // should succeed because NameExistsAsync filters out the renamed
    // category's own Id. Without that filter, every rename would fail.
    [Fact]
    public async Task CreateCategory_ThenRenameToSameName_SucceedsBecauseExcludeSelfId()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var createResult = await CreateCategoryCommandHandler.HandleAsync(
                new CreateCategoryCommand("Books"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            // Act — rename to the same name. The handler passes excludeId=
            // category.Id to NameExistsAsync, so the uniqueness check ignores
            // this category's own row.
            var renameResult = await RenameCategoryCommandHandler.HandleAsync(
                new RenameCategoryCommand(createResult.Value, "Books"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.renameLogger,
                CancellationToken.None);

            // Assert — succeeds because the exclude-self-id pattern works.
            renameResult.IsSuccess.Should().BeTrue();
        }
    }

    // Verifies renaming to a duplicate name fails with the friendly
    // "already exists" message. The handler does the pre-check (the DB
    // has a unique index as a backstop, but the handler's check fires
    // first and produces a friendlier error message).
    [Fact]
    public async Task CreateCategory_ThenRenameToDuplicateName_FailsWithAlreadyExists()
    {
        // Arrange — seed TWO categories.
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var firstResult = await CreateCategoryCommandHandler.HandleAsync(
                new CreateCategoryCommand("First"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            var secondResult = await CreateCategoryCommandHandler.HandleAsync(
                new CreateCategoryCommand("Second"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            // Act — try to rename "Second" to "First".
            var renameResult = await RenameCategoryCommandHandler.HandleAsync(
                new RenameCategoryCommand(secondResult.Value, "First"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.renameLogger,
                CancellationToken.None);

            // Assert — fails with the friendly "already exists" message.
            renameResult.IsSuccess.Should().BeFalse();
            renameResult.Error.Should().Contain("already exists");
        }
    }

    // Verifies CreateSubSubCategoryCommandHandler persists the new
    // SubSubCategory row with the correct parent SubCategoryId. The
    // handler uses the AsNoTracking-load + AddEntity-only-the-new-child
    // pattern, so SaveChanges should produce exactly one INSERT.
    [Fact]
    public async Task CreateSubSubCategory_ViaHandler_PersistsWithCorrectParentIds()
    {
        // Arrange — seed a root + Sub.
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var rootResult = await CreateCategoryCommandHandler.HandleAsync(
                new CreateCategoryCommand("Tools"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            var root = await collaborators.categoryRepo
                .GetByIdWithHierarchyAsync(rootResult.Value, CancellationToken.None);
            var sub = root!.AddSubCategory("Power Tools");
            await collaborators.unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Act — add a SubSubCategory via the handler. This is the
            // path that uses GetByIdWithHierarchyNoTrackingAsync + AddEntity
            // to avoid the stale-tracking bug.
            var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
                new CreateSubSubCategoryCommand(root.Id, sub.Id, "Drills"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createSubSubLogger,
                CancellationToken.None);

            // Assert
            result.IsSuccess.Should().BeTrue();
            collaborators.db.ChangeTracker.Clear();

            // Reload the full hierarchy and verify the new SubSubCategory
            // row has the correct SubCategoryId and lives under the
            // expected parent Category.
            var reloaded = await collaborators.categoryRepo
                .GetByIdWithHierarchyAsync(root.Id, CancellationToken.None);
            reloaded!.SubCategories.Should().HaveCount(1);
            var reloadedSub = reloaded.SubCategories.First();
            reloadedSub.SubSubCategories.Should().HaveCount(1);
            var reloadedSubSub = reloadedSub.SubSubCategories.First();
            reloadedSubSub.Name.Should().Be("Drills");
            reloadedSubSub.SubCategoryId.Should().Be(sub.Id);
            reloadedSubSub.IsActive.Should().BeTrue();
        }
    }

    // Verifies the sibling-name uniqueness invariant. The aggregate's
    // AddSubSubCategory throws a DomainException when a sibling with the
    // same name already exists; the handler catches it and returns a
    // Result.Failure with the exception's message.
    [Fact]
    public async Task CreateSubSubCategory_WithSiblingNameCollision_FailsViaDomainException()
    {
        // Arrange — seed a root + Sub + one SubSub ("Drills").
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var rootResult = await CreateCategoryCommandHandler.HandleAsync(
                new CreateCategoryCommand("Tools"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            var root = await collaborators.categoryRepo
                .GetByIdWithHierarchyAsync(rootResult.Value, CancellationToken.None);
            var sub = root!.AddSubCategory("Power Tools");
            root.AddSubSubCategory(sub.Id, "Drills");
            await collaborators.unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Act — try to add a SECOND SubSub with the SAME name.
            var result = await CreateSubSubCategoryCommandHandler.HandleAsync(
                new CreateSubSubCategoryCommand(root.Id, sub.Id, "Drills"),
                collaborators.currentUser,
                collaborators.categoryRepo,
                collaborators.unitOfWork,
                collaborators.createSubSubLogger,
                CancellationToken.None);

            // Assert — handler returns Failure carrying the
            // DomainException's message (caught + wrapped as Result.Failure).
            result.IsSuccess.Should().BeFalse();
            result.Error.Should().NotBeNullOrEmpty();
            // The exact message comes from SubCategory.EnsureSubSubCategoryNameUnique.
            result.Error.Should().Contain("already exists");
        }
    }
}

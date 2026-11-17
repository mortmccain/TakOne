using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Categories.Commands.ImportCategories;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.ImportCategories;

/// <summary>
/// Unit tests for <see cref="ImportCategoriesCommandHandler"/> — the
/// Settings → admin tools → categories CSV import.
///
/// COVERAGE APPROACH:
///   The handler's persistence is deliberately INDIRECT (batched:
///   AddAsync for new roots, IUnitOfWork.AddEntity for new children of
///   pre-existing untracked categories), so the tests capture WHAT was
///   registered rather than asserting against a database: the fake
///   repository records every <c>AddAsync</c> root, and a spy UnitOfWork
///   records every <c>AddEntity</c> child. The domain invariants are
///   exercised for real — every creation goes through the
///   <see cref="Category"/> aggregate, so a test that creates a chain
///   under a deactivated parent fails exactly like production would.
/// </summary>
public class ImportCategoriesCommandHandlerTests
{
    // ── Test doubles ────────────────────────────────────────────────────

    /// <summary>
    /// A UnitOfWork spy that records AddEntity calls (the handler's
    /// registration path for new children under PRE-EXISTING categories)
    /// and counts SaveChanges.
    /// </summary>
    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public List<object> AddedEntities { get; } = new();

        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(AddedEntities.Count);
        }

        public void ClearChangeTracker() { /* no-op for tests */ }

        public void AddEntity(object entity) => AddedEntities.Add(entity);

        public Task<T> ExecuteWithRetryAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            int maxAttempts = 3,
            CancellationToken cancellationToken = default)
            => operation(cancellationToken);
    }

    private static (ICurrentUserService currentUser, ICategoryRepository repo, RecordingUnitOfWork uow, ILogger<ImportCategoriesCommandHandler> logger)
        BuildMocks(bool admin = true, List<Category>? existingTree = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CustomerId);
        currentUser.IsInRole(Arg.Any<string>()).Returns(false);
        if (admin)
        {
            currentUser.IsInRole(Roles.Admin).Returns(true);
        }

        var repo = Substitute.For<ICategoryRepository>();
        repo.GetAllWithHierarchyNoTrackingAsync(Arg.Any<CancellationToken>())
            .Returns(existingTree ?? new List<Category>());

        var logger = Substitute.For<ILogger<ImportCategoriesCommandHandler>>();

        return (currentUser, repo, new RecordingUnitOfWork(), logger);
    }

    private static List<Category> AddedRoots(ICategoryRepository repo)
        => repo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ICategoryRepository.AddAsync))
            .Select(c => c.GetArguments().FirstOrDefault(a => a is Category))
            .Cast<Category>()
            .ToList();

    private static CategoryImportRowDto Row(
        int number, string category, string? sub = null, string? subSub = null)
        => new()
        {
            RowNumber = number,
            CategoryName = category,
            SubCategoryName = sub,
            SubSubCategoryName = subSub
        };

    // ── Auth + shape guards ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Unauthenticated_RejectsWithoutTouchingTheTree()
    {
        var (currentUser, repo, uow, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[] { Row(1, "Toys") }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await repo.DidNotReceive().GetAllWithHierarchyNoTrackingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NonAdmin_Rejected()
    {
        var (currentUser, repo, uow, logger) = BuildMocks(admin: false);

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[] { Row(1, "Toys") }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Only administrators may import categories.");
    }

    [Fact]
    public async Task HandleAsync_EmptyRows_Rejected()
    {
        var (currentUser, repo, uow, logger) = BuildMocks();

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(Array.Empty<CategoryImportRowDto>()),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("no data rows");
    }

    [Fact]
    public async Task HandleAsync_RowCapExceeded_Rejected()
    {
        var (currentUser, repo, uow, logger) = BuildMocks();
        var rows = Enumerable.Range(1, 2001)
            .Select(i => Row(i, $"C{i}"))
            .ToArray();

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(rows),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("maximum");
    }

    // ── Find-or-create semantics ────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_EmptyTree_CreatesFullChainAsOneRoot()
    {
        // A row with the full triple against an empty DB: ONE new root
        // category is registered (its sub + subsub ride the Added graph —
        // AddEntity is NOT used for them).
        var (currentUser, repo, uow, logger) = BuildMocks();

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[] { Row(1, "Toys", "Outdoor", "Balls") }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CreatedCount.Should().Be(1);
        result.Value.RowResults[0].Outcome.Should().Be(CategoryImportRowOutcome.Created);

        var roots = AddedRoots(repo);
        roots.Should().ContainSingle("one new root category was registered");
        roots[0].Name.Should().Be("Toys");
        roots[0].SubCategories.Should().ContainSingle(s => s.Name == "Outdoor");
        roots[0].SubCategories[0].SubSubCategories.Should().ContainSingle(ss => ss.Name == "Balls");

        uow.AddedEntities.Should().BeEmpty(
            "children of a NEW root ride the root's Added graph — no AddEntity needed");
        uow.SaveCount.Should().Be(1, "a single SaveChanges commits the whole run");
    }

    [Fact]
    public async Task HandleAsync_FullyExistingChain_ReportsExistedWithoutRegisteringAnything()
    {
        // Round-trip idempotence: the whole chain already exists → the row
        // is a no-op ("existed"), and NOTHING is registered for persistence.
        var toys = Category.Create("Toys");
        var outdoor = toys.AddSubCategory("Outdoor");
        toys.AddSubSubCategory(outdoor.Id, "Balls");

        var (currentUser, repo, uow, logger) = BuildMocks(existingTree: new List<Category> { toys });

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[] { Row(1, "Toys", "Outdoor", "Balls") }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ExistedCount.Should().Be(1);
        result.Value.CreatedCount.Should().Be(0);
        result.Value.RowResults[0].Outcome.Should().Be(CategoryImportRowOutcome.Existed);

        AddedRoots(repo).Should().BeEmpty("nothing new was created");
        uow.AddedEntities.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_ExistingCategoryNewChildren_RegistersChildrenIndividually()
    {
        // The documented stale-tracking pattern in action: the category
        // exists (loaded untracked); its NEW sub + subsub are registered
        // via IUnitOfWork.AddEntity — the category itself is never
        // re-registered (no spurious UPDATE possible).
        var toys = Category.Create("Toys");
        var (currentUser, repo, uow, logger) = BuildMocks(existingTree: new List<Category> { toys });

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[] { Row(1, "Toys", "Outdoor", "Balls") }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.Value.CreatedCount.Should().Be(1);
        AddedRoots(repo).Should().BeEmpty("the category already exists — no AddAsync");
        uow.AddedEntities.Should().HaveCount(2,
            "the new SubCategory and the new SubSubCategory are each registered via AddEntity");
        uow.AddedEntities.OfType<SubCategory>()
            .Should().ContainSingle(s => s.Name == "Outdoor");
        uow.AddedEntities.OfType<SubSubCategory>()
            .Should().ContainSingle(ss => ss.Name == "Balls");
    }

    [Fact]
    public async Task HandleAsync_NameMatching_IsCaseInsensitiveAndTrims()
    {
        var electronics = Category.Create("Electronics");
        var (currentUser, repo, uow, logger) = BuildMocks(
            existingTree: new List<Category> { electronics });

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[] { Row(1, "  electronics  ") }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.Value.ExistedCount.Should().Be(1,
            "matching is case-insensitive on trimmed names — no duplicate category is created");
        AddedRoots(repo).Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_TwoRowsCreatingSameCategory_ResolveToOneInstance()
    {
        // In-run dedupe: the second row finds the FIRST row's in-memory
        // creation and only creates its own new node — one root registered
        // in total, and the sub rides that root's Added graph (no AddEntity:
        // AddEntity is exclusively for children of PRE-EXISTING categories).
        var (currentUser, repo, uow, logger) = BuildMocks();

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[]
            {
                Row(1, "Toys"),
                Row(2, "Toys", "Outdoor")
            }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.Value.CreatedCount.Should().Be(2);
        result.Value.ExistedCount.Should().Be(0);
        AddedRoots(repo).Should().ContainSingle(
            "both rows resolved to the same in-memory category instance");
        AddedRoots(repo)[0].SubCategories.Should().ContainSingle(s => s.Name == "Outdoor");
        uow.AddedEntities.Should().BeEmpty(
            "the sub belongs to a run-created root — it rides the root's Added graph");
    }

    // ── Deactivated nodes + markers ─────────────────────────────────────

    [Fact]
    public async Task HandleAsync_SubUnderDeactivatedCategory_FailsWithReasonAndRegistersNothing()
    {
        // The aggregate's EnsureActive guard rejects children under a
        // deactivated category — the row fails with the domain's message
        // and NOTHING is registered for it.
        var toys = Category.Create("Toys");
        toys.Deactivate();
        var (currentUser, repo, uow, logger) = BuildMocks(existingTree: new List<Category> { toys });

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[] { Row(1, "Toys", "Outdoor") }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.Value.FailedCount.Should().Be(1);
        result.Value.RowResults[0].Outcome.Should().Be(CategoryImportRowOutcome.Failed);
        result.Value.RowResults[0].Message.Should().Contain("deactivated");
        uow.AddedEntities.Should().BeEmpty(
            "the failed row must not leave a half-registered chain behind");
    }

    [Fact]
    public async Task HandleAsync_DeactivatedMarkerOnCategoryRow_MatchesAndReportsExisted()
    {
        // Round-trip proof: the export appends " (deactivated)" to a
        // deactivated node's name; the import strips it (BOTH culture
        // variants) and matches the (still deactivated) node — a clean
        // no-op row, never a duplicate category, never a reactivation.
        var toys = Category.Create("Toys");
        toys.Deactivate();
        var (currentUser, repo, uow, logger) = BuildMocks(existingTree: new List<Category> { toys });

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[]
            {
                Row(1, "Toys (deactivated)"),
                Row(2, "Toys (غیرفعال)")
            }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.Value.FailedCount.Should().Be(0, "marker-suffixed names match their tree node");
        result.Value.ExistedCount.Should().Be(2);
        toys.IsActive.Should().BeFalse("the import never changes activation state");
        AddedRoots(repo).Should().BeEmpty("no duplicate category was created");
    }

    // ── Per-row shape rules ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_SubSubWithoutSub_FailsPerRow()
    {
        var (currentUser, repo, uow, logger) = BuildMocks();

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[]
            {
                Row(1, "Toys", subSub: "Balls"), // sub missing — contradiction
                Row(2, "Games")                  // next row still processes
            }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.Value.FailedCount.Should().Be(1);
        result.Value.RowResults[0].Message.Should().Contain("without a sub-category");
        result.Value.CreatedCount.Should().Be(1, "the other row is unaffected (best-effort)");
    }

    [Fact]
    public async Task HandleAsync_EmptyCategoryName_FailsPerRow()
    {
        var (currentUser, repo, uow, logger) = BuildMocks();

        var result = await ImportCategoriesCommandHandler.HandleAsync(
            new ImportCategoriesCommand(new[] { Row(1, "   ") }),
            currentUser, repo, uow, logger, CancellationToken.None);

        result.Value.FailedCount.Should().Be(1);
        result.Value.RowResults[0].Message.Should().Contain("empty");
    }
}

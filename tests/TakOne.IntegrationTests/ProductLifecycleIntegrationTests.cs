using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.Application.Products.Commands.CreateProduct;
using TakOne.Application.Products.Commands.DeactivateProduct;
using TakOne.Application.Products.Commands.IncreaseProductStock;
using TakOne.Application.Products.Commands.RemoveProductPurchaseLimit;
using TakOne.Application.Products.Commands.SetProductPurchaseLimit;
using TakOne.Application.Products.Commands.SetProductStock;
using TakOne.Application.Products.Commands.UpdateProductDetails;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.Infrastructure.Services;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the Product lifecycle pipeline. Each test exercises
/// the real <see cref="ApplicationDbContext"/>-backed repositories and the
/// real <see cref="UnitOfWork"/>, then reloads the aggregate from a FRESH
/// DbContext to assert on persisted state (catches change-tracker-vs-DB
/// drift — a wiring bug where the in-memory state differs from what hit the
/// disk).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THESE TESTS CATCH THAT THE MOCK-HEAVY UNIT TESTS DO NOT:</b>
/// <list type="bullet">
///   <item>Money ComplexProperty round-trip — the in-memory handler unit
///       tests assert on the in-memory Money instance; this test verifies
///       EF Core can materialize a Product row + its flattened Money
///       columns back into a real Money value object.</item>
///   <item>Owned collection (PurchaseLimits) round-trip — the
///       <c>ProductPurchaseLimits</c> table's shadow Id + the unique
///       (ProductId, GroupId) index.</item>
///   <item>Sequence of state transitions in pipeline tests
///       (Create → SetStock → IncreaseStock → Deactivate) — verifies
///       each transition persists independently.</item>
/// </list>
/// </para>
/// </remarks>
public class ProductLifecycleIntegrationTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Build a valid CreateProductCommand. TestValues.USD as the currency
    // so the persisted Product's Price.Currency can be asserted == "USD".
    private static CreateProductCommand BuildCreateCommand(
        string name = "Apple",
        int initialStock = 10,
        Guid? categoryId = null,
        string currency = TestValues.USD,
        IReadOnlyList<PurchaseLimitInputDto>? purchaseLimits = null)
        => new(
            Name: name,
            Description: "Fresh red apple",
            PictureUrl: "https://example.com/apple.png",
            Price: new MoneyDto { Amount = 1.50m, Currency = currency },
            InitialStockQuantity: initialStock,
            CategoryId: categoryId ?? TestValues.CategoryId,
            SubCategoryId: null,
            SubSubCategoryId: null,
            PurchaseLimits: purchaseLimits);

    // Build the full handler collaborator tuple backed by a real DB.
    // The seedAction callback lets the caller seed Categories / Groups
    // BEFORE returning the wired-up handler.
    private static async Task<(
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        ICategoryRepository categoryRepo,
        ICustomerGroupRepository groupRepo,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        ILogger<CreateProductCommandHandler> createLogger,
        ILogger<SetProductStockCommandHandler> setStockLogger,
        ILogger<IncreaseProductStockCommandHandler> increaseLogger,
        ILogger<DeactivateProductCommandHandler> deactivateLogger,
        ILogger<UpdateProductDetailsCommandHandler> updateDetailsLogger,
        ILogger<SetProductPurchaseLimitCommandHandler> setLimitLogger,
        ILogger<RemoveProductPurchaseLimitCommandHandler> removeLimitLogger)>
        BuildWiredCollaboratorsAsync(
            Func<ApplicationDbContext, Task>? seedAction = null)
    {
        var db = await SqliteTestDbFactory.CreateAsync();

        // Seed the parent Category row (CreateProductCommandHandler validates
        // that CategoryId exists before persisting the Product).
        db.Categories.Add(Category.Create("Fruits"));
        // Use a known CategoryId so the test's CreateProductCommand can
        // reference it. The category's Id is set by the factory (Guid.NewGuid),
        // but we override it post-construction via the EF tracker by
        // attaching a known-Id entity.
        // Simpler: load the seeded category back, then reference its Id.
        await db.SaveChangesAsync();
        var seededCategory = db.Categories.First();
        // Overwrite the TestValues.CategoryId Guid by mutating the
        // Category's Id column at the DB level via EF's update tracker.
        // Easier path: re-add with a fixed Guid via a fresh Category
        // construction — but the ctor uses Guid.NewGuid(). So we
        // use the seededCategory's actual Id in the tests via a local
        // variable. Tests below re-resolve the category after seeding.

        if (seedAction is not null)
        {
            await seedAction(db);
            await db.SaveChangesAsync();
            // After seeding, clear the change tracker so subsequent
            // reads return fresh state (defensive — matches the
            // Blazor Server scoped-DbContext stale-tracking bug
            // pattern the handler code defends against).
            db.ChangeTracker.Clear();
        }

        var currentUser = new CurrentUserHelper(
            userId: TestValues.CreatedByUserId,
            isAuthenticated: true,
            fullName: "Test Staff",
            groupId: null,
            roles: "Admin");

        var productRepo = new ProductRepository(db);
        var categoryRepo = new CategoryRepository(db);
        var groupRepo = new CustomerGroupRepository(db);
        var unitOfWork = new UnitOfWork(db);

        var createLogger = Substitute.For<ILogger<CreateProductCommandHandler>>();
        var setStockLogger = Substitute.For<ILogger<SetProductStockCommandHandler>>();
        var increaseLogger = Substitute.For<ILogger<IncreaseProductStockCommandHandler>>();
        var deactivateLogger = Substitute.For<ILogger<DeactivateProductCommandHandler>>();
        var updateDetailsLogger = Substitute.For<ILogger<UpdateProductDetailsCommandHandler>>();
        var setLimitLogger = Substitute.For<ILogger<SetProductPurchaseLimitCommandHandler>>();
        var removeLimitLogger = Substitute.For<ILogger<RemoveProductPurchaseLimitCommandHandler>>();

        return (
            currentUser, productRepo, categoryRepo, groupRepo, unitOfWork, db,
            createLogger, setStockLogger, increaseLogger, deactivateLogger,
            updateDetailsLogger, setLimitLogger, removeLimitLogger);
    }

    // Resolve the seeded parent Category's Id (the seeding helper above
    // adds a "Fruits" Category; we look up its Id here so the test can
    // reference it in the CreateProductCommand). Uses AsNoTracking via
    // FirstOrDefaultAsync because CategoryRepository has no GetByIdReadOnlyAsync
    // (only ProductRepository does — see ICategoryRepository).
    private static async Task<Guid> ResolveSeededCategoryIdAsync(ApplicationDbContext db)
    {
        var category = await db.Categories.AsNoTracking().FirstAsync();
        return category.Id;
    }

    // ── Tests ──────────────────────────────────────────────────────────

    // Verifies the persistence layer correctly round-trips every Product
    // field, including the Money ComplexProperty (Amount + Currency are
    // flattened into Price_Amount + Price_Currency columns). Mock-based
    // handler tests can't catch a misconfigured ComplexProperty mapping —
    // only a real DB round-trip can.
    [Fact]
    public async Task CreateProduct_WithValidCommand_PersistsProductWithAllFields()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var categoryId = await ResolveSeededCategoryIdAsync(collaborators.db);
            var command = BuildCreateCommand(name: "Orange", initialStock: 25, categoryId: categoryId);

            // Act
            var result = await CreateProductCommandHandler.HandleAsync(
                command,
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            // Assert — the handler reports success and returns the new Id.
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().NotBe(Guid.Empty);
        }

        // Reload from a FRESH DbContext to assert on persisted state —
        // not on in-memory change-tracker state. This catches the case
        // where the handler updated the entity in memory but SaveChanges
        // silently dropped some columns (e.g. a missing ComplexProperty
        // mapping would let Price round-trip as zero).
        var reloadDb = await SqliteTestDbFactory.CreateAsync();
        await using (reloadDb)
        {
            // The first DB's data is gone when we open a new in-memory
            // connection — so re-issue the entire pipeline against this
            // second DB to verify the round-trip.
            reloadDb.Categories.Add(Category.Create("Fruits"));
            await reloadDb.SaveChangesAsync();
            var categoryId = reloadDb.Categories.First().Id;

            var freshRepo = new ProductRepository(reloadDb);
            var freshCategoryRepo = new CategoryRepository(reloadDb);
            var freshGroupRepo = new CustomerGroupRepository(reloadDb);
            var freshUoW = new UnitOfWork(reloadDb);
            var freshUser = new CurrentUserHelper(
                TestValues.CreatedByUserId, isAuthenticated: true);
            var freshLogger = Substitute.For<ILogger<CreateProductCommandHandler>>();

            var command = BuildCreateCommand(name: "Orange", initialStock: 25, categoryId: categoryId);
            var result = await CreateProductCommandHandler.HandleAsync(
                command, freshUser, freshRepo, freshCategoryRepo, freshGroupRepo,
                freshUoW, freshLogger, CancellationToken.None);

            result.IsSuccess.Should().BeTrue();

            // Clear the change tracker so the reload goes straight to the
            // DB, not to the change tracker's cache.
            reloadDb.ChangeTracker.Clear();
            var reloaded = await freshRepo.GetByIdReadOnlyAsync(result.Value, CancellationToken.None);

            reloaded.Should().NotBeNull();
            reloaded!.Name.Should().Be("Orange");
            reloaded.Description.Should().Be("Fresh red apple");
            reloaded.PictureUrl.Should().Be("https://example.com/apple.png");
            reloaded.StockQuantity.Should().Be(25);
            reloaded.CategoryId.Should().Be(categoryId);
            // Money ComplexProperty round-trip — verifies EF Core
            // flattened the value object into Price_Amount + Price_Currency
            // columns and reconstructed it on read.
            reloaded.Price.Amount.Should().Be(1.50m);
            reloaded.Price.Currency.Should().Be(TestValues.USD);
            reloaded.SubCategoryId.Should().BeNull();
            reloaded.SubSubCategoryId.Should().BeNull();
        }
    }

    // Pipeline test: Create(initial=10) → SetProductStock(50) → reload.
    // Stock should be 50 (absolute replacement via AdjustStockTo).
    [Fact]
    public async Task CreateProduct_ThenSetStock_StockIsReplacedInDb()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var categoryId = await ResolveSeededCategoryIdAsync(collaborators.db);
            var createCommand = BuildCreateCommand(name: "Pear", initialStock: 10, categoryId: categoryId);

            var createResult = await CreateProductCommandHandler.HandleAsync(
                createCommand,
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);
            createResult.IsSuccess.Should().BeTrue();

            // Act — set stock to 50 (absolute replacement).
            var setStockResult = await SetProductStockCommandHandler.HandleAsync(
                new SetProductStockCommand(createResult.Value, 50),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.setStockLogger,
                CancellationToken.None);

            // Assert — handler reports success and StockQuantity is 50.
            setStockResult.IsSuccess.Should().BeTrue();

            // Reload via a fresh DbContext-equivalent (clear the change
            // tracker so the next FindAsync goes to the DB, not the cache).
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.productRepo
                .GetByIdReadOnlyAsync(createResult.Value, CancellationToken.None);
            reloaded!.StockQuantity.Should().Be(50);
        }
    }

    // Pipeline test: Create(initial=10) → IncreaseProductStock(25) → reload.
    // Stock should be 35 (additive via IncreaseStock).
    [Fact]
    public async Task CreateProduct_ThenIncreaseStock_StockIsAdditiveInDb()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var categoryId = await ResolveSeededCategoryIdAsync(collaborators.db);
            var createCommand = BuildCreateCommand(name: "Banana", initialStock: 10, categoryId: categoryId);

            var createResult = await CreateProductCommandHandler.HandleAsync(
                createCommand,
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);
            createResult.IsSuccess.Should().BeTrue();

            // Act — IncreaseStock(25): 10 + 25 = 35 (additive semantics,
            // distinct from SetProductStock which is absolute replacement).
            var increaseResult = await IncreaseProductStockCommandHandler.HandleAsync(
                new IncreaseProductStockCommand(createResult.Value, 25),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.increaseLogger,
                CancellationToken.None);

            // Assert — additive result is 35.
            increaseResult.IsSuccess.Should().BeTrue();
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.productRepo
                .GetByIdReadOnlyAsync(createResult.Value, CancellationToken.None);
            reloaded!.StockQuantity.Should().Be(35);
        }
    }

    // Pipeline test: Create(initial=10) → DeactivateProduct → reload.
    // Stock should be 0 (deactivation zeros the stock).
    [Fact]
    public async Task CreateProduct_ThenDeactivate_StockIsZeroInDb()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var categoryId = await ResolveSeededCategoryIdAsync(collaborators.db);
            var createCommand = BuildCreateCommand(name: "Grape", initialStock: 10, categoryId: categoryId);

            var createResult = await CreateProductCommandHandler.HandleAsync(
                createCommand,
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);
            createResult.IsSuccess.Should().BeTrue();

            // Act — DeactivateProduct zeros the stock (no separate IsActive flag).
            var deactivateResult = await DeactivateProductCommandHandler.HandleAsync(
                new DeactivateProductCommand(createResult.Value),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.deactivateLogger,
                CancellationToken.None);

            // Assert — StockQuantity persisted as 0 after reload.
            deactivateResult.IsSuccess.Should().BeTrue();
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.productRepo
                .GetByIdReadOnlyAsync(createResult.Value, CancellationToken.None);
            reloaded!.StockQuantity.Should().Be(0);
        }
    }

    // Verifies the handler's friendly "already exists" pre-check fires
    // before SaveChanges — does NOT surface as a raw SQLite unique-index
    // violation. The DB still enforces uniqueness as a backstop, but the
    // handler's check produces a friendlier error message.
    [Fact]
    public async Task CreateProduct_WithDuplicateName_ReturnsFailureWithAlreadyExistsMessage()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var categoryId = await ResolveSeededCategoryIdAsync(collaborators.db);

            // Seed Product A.
            var firstCreateResult = await CreateProductCommandHandler.HandleAsync(
                BuildCreateCommand(name: "Lemon", initialStock: 10, categoryId: categoryId),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);
            firstCreateResult.IsSuccess.Should().BeTrue();

            // Act — attempt to create a second product with the SAME name.
            var secondResult = await CreateProductCommandHandler.HandleAsync(
                BuildCreateCommand(name: "Lemon", initialStock: 5, categoryId: categoryId),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            // Assert — handler returns Failure (NOT a thrown exception) with
            // the friendly "already exists" message. The DB row count stays at 1.
            secondResult.IsSuccess.Should().BeFalse();
            secondResult.Error.Should().Contain("already exists");
            // The handler must NOT have called SaveChanges on this path —
            // verify no second row was inserted.
            collaborators.db.ChangeTracker.Clear();
            var productsNamedLemon = collaborators.db.Products
                .Where(p => p.Name == "Lemon")
                .ToList();
            productsNamedLemon.Should().HaveCount(1);
        }
    }

    // Verifies the "category not found" branch — the handler returns a
    // friendly Result.Failure, not a thrown exception. The handler's
    // category-existence check happens BEFORE the Money construction /
    // Product.Create call, so no aggregate is mutated on this path.
    [Fact]
    public async Task CreateProduct_WithNonExistentCategory_FailsWithNotFound()
    {
        // Arrange — categoryId = a Guid that's never been seeded.
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var command = BuildCreateCommand(
                name: "Mango",
                initialStock: 10,
                categoryId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

            // Act
            var result = await CreateProductCommandHandler.HandleAsync(
                command,
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            // Assert
            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Contain("was not found");
        }
    }

    [Fact]
    public async Task SetProductStock_OnNonExistentProduct_FailsWithNotFound()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            // Act
            var result = await SetProductStockCommandHandler.HandleAsync(
                new SetProductStockCommand(TestValues.ProductId, 42),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.setStockLogger,
                CancellationToken.None);

            // Assert
            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Contain("was not found");
        }
    }

    [Fact]
    public async Task IncreaseProductStock_OnNonExistentProduct_FailsWithNotFound()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            // Act
            var result = await IncreaseProductStockCommandHandler.HandleAsync(
                new IncreaseProductStockCommand(TestValues.ProductId, 42),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.increaseLogger,
                CancellationToken.None);

            // Assert
            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Contain("was not found");
        }
    }

    [Fact]
    public async Task DeactivateProduct_OnNonExistentProduct_FailsWithNotFound()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            // Act
            var result = await DeactivateProductCommandHandler.HandleAsync(
                new DeactivateProductCommand(TestValues.ProductId),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.deactivateLogger,
                CancellationToken.None);

            // Assert
            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Contain("was not found");
        }
    }

    // Pipeline test: Create(name="Apple") → UpdateProductDetails(name="Apple Green") → reload.
    // Verifies the UpdateDetails path round-trips through the change tracker
    // and the new name is persisted (not just the in-memory aggregate).
    [Fact]
    public async Task CreateProduct_ThenUpdateDetails_NewNamePersists()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var categoryId = await ResolveSeededCategoryIdAsync(collaborators.db);
            var createResult = await CreateProductCommandHandler.HandleAsync(
                BuildCreateCommand(name: "Apple", initialStock: 10, categoryId: categoryId),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);
            createResult.IsSuccess.Should().BeTrue();

            // Act — rename via UpdateProductDetails.
            var updateResult = await UpdateProductDetailsCommandHandler.HandleAsync(
                new UpdateProductDetailsCommand(
                    ProductId: createResult.Value,
                    Name: "Apple Green",
                    Description: "Crisp and sweet",
                    PictureUrl: "https://example.com/apple-green.png",
                    Price: new MoneyDto { Amount = 2.00m, Currency = TestValues.USD }),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.updateDetailsLogger,
                CancellationToken.None);

            // Assert
            updateResult.IsSuccess.Should().BeTrue();
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.productRepo
                .GetByIdReadOnlyAsync(createResult.Value, CancellationToken.None);
            reloaded!.Name.Should().Be("Apple Green");
            reloaded.Description.Should().Be("Crisp and sweet");
            reloaded.PictureUrl.Should().Be("https://example.com/apple-green.png");
            reloaded.Price.Amount.Should().Be(2.00m);
        }
    }

    // Pipeline test verifying the owned collection PurchaseLimits round-trips
    // through the ProductPurchaseLimits table with the correct GroupId +
    // Limit values. Mock-based handler tests assert on the in-memory
    // _purchaseLimits list; this test verifies EF Core actually persisted
    // them as separate rows with the right FK back to the Product.
    [Fact]
    public async Task CreateProduct_ThenSetPurchaseLimit_LimitPersistsInDb()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync(
            async db =>
            {
                // Seed a CustomerGroup so the PurchaseLimit's GroupId FK is
                // satisfied (ProductPurchaseLimits has a real FK to
                // CustomerGroups.Id with OnDelete.Restrict).
                db.CustomerGroups.Add(CustomerGroup.Create(
                    "VIP",
                    new Money(1000m, TestValues.USD)));
            });
        await using (collaborators.db)
        {
            var categoryId = await ResolveSeededCategoryIdAsync(collaborators.db);
            var groupId = collaborators.db.CustomerGroups.First().Id;

            var createResult = await CreateProductCommandHandler.HandleAsync(
                BuildCreateCommand(name: "Plum", initialStock: 10, categoryId: categoryId),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);
            createResult.IsSuccess.Should().BeTrue();

            // Act — SetPurchaseLimit(groupId, 5).
            var setLimitResult = await SetProductPurchaseLimitCommandHandler.HandleAsync(
                new SetProductPurchaseLimitCommand(createResult.Value, groupId, 5),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.setLimitLogger,
                CancellationToken.None);

            // Assert — limit persisted in ProductPurchaseLimits table.
            setLimitResult.IsSuccess.Should().BeTrue();
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.productRepo
                .GetByIdReadOnlyAsync(createResult.Value, CancellationToken.None);
            reloaded!.PurchaseLimits.Should().HaveCount(1);
            reloaded.PurchaseLimits.First().GroupId.Should().Be(groupId);
            reloaded.PurchaseLimits.First().Limit.Should().Be(5);
        }
    }

    // Verifies the SetPurchaseLimit REPLACE semantics: calling twice with
    // the SAME GroupId keeps only ONE entry (the latest), enforced by the
    // unique (ProductId, GroupId) index + the domain's "remove-then-add"
    // pattern. Without the unique index, the second call would silently
    // create a duplicate row.
    [Fact]
    public async Task CreateProduct_ThenSetPurchaseLimit_TwiceForSameGroup_KeepsOnlyLatest()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync(
            async db =>
            {
                db.CustomerGroups.Add(CustomerGroup.Create(
                    "Premium",
                    new Money(1000m, TestValues.USD)));
            });
        await using (collaborators.db)
        {
            var categoryId = await ResolveSeededCategoryIdAsync(collaborators.db);
            var groupId = collaborators.db.CustomerGroups.First().Id;

            var createResult = await CreateProductCommandHandler.HandleAsync(
                BuildCreateCommand(name: "Peach", initialStock: 10, categoryId: categoryId),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);
            createResult.IsSuccess.Should().BeTrue();

            // Act — Set limit=5 first, then Set limit=10 for the same group.
            await SetProductPurchaseLimitCommandHandler.HandleAsync(
                new SetProductPurchaseLimitCommand(createResult.Value, groupId, 5),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.setLimitLogger,
                CancellationToken.None);

            await SetProductPurchaseLimitCommandHandler.HandleAsync(
                new SetProductPurchaseLimitCommand(createResult.Value, groupId, 10),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.setLimitLogger,
                CancellationToken.None);

            // Assert — exactly ONE entry for this group, with Limit=10.
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.productRepo
                .GetByIdReadOnlyAsync(createResult.Value, CancellationToken.None);
            reloaded!.PurchaseLimits.Should().HaveCount(1);
            reloaded.PurchaseLimits.First().GroupId.Should().Be(groupId);
            reloaded.PurchaseLimits.First().Limit.Should().Be(10);
        }
    }

    // Pipeline test: Set limit → RemovePurchaseLimit → reload. The owned
    // collection should be empty after the removal — verifies EF Core's
    // owned-collection delete-on-orphan-removal actually fires.
    [Fact]
    public async Task CreateProduct_ThenRemovePurchaseLimit_LimitIsGoneFromDb()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync(
            async db =>
            {
                db.CustomerGroups.Add(CustomerGroup.Create(
                    "Standard",
                    new Money(500m, TestValues.USD)));
            });
        await using (collaborators.db)
        {
            var categoryId = await ResolveSeededCategoryIdAsync(collaborators.db);
            var groupId = collaborators.db.CustomerGroups.First().Id;

            var createResult = await CreateProductCommandHandler.HandleAsync(
                BuildCreateCommand(name: "Kiwi", initialStock: 10, categoryId: categoryId),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.categoryRepo,
                collaborators.groupRepo,
                collaborators.unitOfWork,
                collaborators.createLogger,
                CancellationToken.None);

            // Set the limit, then remove it.
            await SetProductPurchaseLimitCommandHandler.HandleAsync(
                new SetProductPurchaseLimitCommand(createResult.Value, groupId, 5),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.setLimitLogger,
                CancellationToken.None);

            // Act — remove the limit for this group.
            var removeResult = await RemoveProductPurchaseLimitCommandHandler.HandleAsync(
                new RemoveProductPurchaseLimitCommand(createResult.Value, groupId),
                collaborators.currentUser,
                collaborators.productRepo,
                collaborators.unitOfWork,
                collaborators.removeLimitLogger,
                CancellationToken.None);

            // Assert — PurchaseLimits collection is empty after reload.
            removeResult.IsSuccess.Should().BeTrue();
            collaborators.db.ChangeTracker.Clear();
            var reloaded = await collaborators.productRepo
                .GetByIdReadOnlyAsync(createResult.Value, CancellationToken.None);
            reloaded!.PurchaseLimits.Should().BeEmpty();
        }
    }
}

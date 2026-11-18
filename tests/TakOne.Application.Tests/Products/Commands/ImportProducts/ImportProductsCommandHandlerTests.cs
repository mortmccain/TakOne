using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.Commands.ImportProducts;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.ImportProducts;

/// <summary>
/// Unit tests for <see cref="ImportProductsCommandHandler"/> — the
/// Settings → admin tools → products CSV import.
///
/// COVERAGE APPROACH: every collaborator is mocked with NSubstitute
/// except the DOMAIN, which runs for real — creations go through
/// <see cref="Product.Create"/> and updates through the aggregate's
/// guard methods, so a row that violates an invariant fails exactly like
/// production. The fake product repository captures what was registered
/// for creation, and updates are asserted directly on the returned
/// aggregate instance (the handler mutates it through the domain).
///
/// PICTURE-RE-LINKING FOCUS: these are the FIRST tests this handler ever
/// had, written for the production incident where every product whose
/// photo name contained a space (or any character outside
/// <c>[A-Za-z0-9._-]</c>) rendered WITHOUT its picture: the bulk upload
/// stores <c>"Pizza flour 500g.png"</c> on disk as
/// <c>Pizzaflour500g.png</c>, but the import used to link the CSV cell
/// verbatim. The handler now normalizes through
/// <see cref="PreservedFileNames"/> — the same rule the upload applies —
/// and these tests pin that contract from the import side.
/// </summary>
public class ImportProductsCommandHandlerTests
{
    // ── Test doubles ────────────────────────────────────────────────────

    /// <summary>
    /// A UnitOfWork double that counts SaveChanges (the handler's single
    /// commit point) — no persistence semantics needed for these tests.
    /// </summary>
    private sealed class CountingUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(0);
        }

        public void ClearChangeTracker() { /* no-op for tests */ }

        public void AddEntity(object entity) { /* no-op for tests */ }

        public Task<T> ExecuteWithRetryAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            int maxAttempts = 3,
            CancellationToken cancellationToken = default)
            => operation(cancellationToken);
    }

    // ── Environment builders ───────────────────────────────────────────

    /// <summary>
    /// The minimal category tree the standard rows need: root
    /// "Flour" → sub "Pizza Flour". Handler matching is name-based and
    /// case-insensitive, exactly like the CSV round trip.
    /// </summary>
    private static List<Category> BuildDefaultCategoryTree()
    {
        var root = Category.Create("Flour");
        root.AddSubCategory("Pizza Flour");
        return [root];
    }

    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        ICategoryRepository categoryRepo,
        CountingUnitOfWork uow,
        ILogger<ImportProductsCommandHandler> logger)
        BuildMocks(
            bool admin = true,
            List<Category>? categoryTree = null,
            List<Product>? existingProducts = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);
        currentUser.IsInRole(Arg.Any<string>()).Returns(admin);

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var name = callInfo.Arg<string>();
                return existingProducts?.FirstOrDefault(
                    p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            });

        var categoryRepo = Substitute.For<ICategoryRepository>();
        categoryRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(categoryTree ?? BuildDefaultCategoryTree());

        return (currentUser, productRepo, categoryRepo, new CountingUnitOfWork(),
            Substitute.For<ILogger<ImportProductsCommandHandler>>());
    }

    /// <summary>A valid row for the default tree; each test overrides what it needs.</summary>
    private static ProductImportRowDto Row(
        int number,
        string name = "Pizza flour 500g",
        string? pictureFileName = "Pizza flour 500g.png",
        string category = "Flour",
        string sub = "Pizza Flour",
        decimal price = 760_000m,
        string currency = "IRR",
        int stock = 0)
        => new()
        {
            RowNumber = number,
            Name = name,
            CategoryName = category,
            SubCategoryName = sub,
            SubSubCategoryName = string.Empty,
            PriceAmount = price,
            Currency = currency,
            StockQuantity = stock,
            PictureFileName = pictureFileName
        };

    private static Task<Result<ProductImportResultDto>> HandleAsync(
        ImportProductsCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        ICategoryRepository categoryRepo,
        CountingUnitOfWork uow,
        ILogger<ImportProductsCommandHandler> logger)
        => ImportProductsCommandHandler.HandleAsync(
            command, currentUser, productRepo, categoryRepo, uow, logger,
            CancellationToken.None);

    private static List<Product> AddedProducts(IProductRepository repo)
        => repo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IProductRepository.AddAsync))
            .Select(c => c.GetArguments().FirstOrDefault(a => a is Product))
            .Cast<Product>()
            .ToList();

    // ── Auth + shape guards ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NonAdmin_Rejected()
    {
        var (currentUser, productRepo, categoryRepo, uow, logger) = BuildMocks(admin: false);

        var result = await HandleAsync(
            new ImportProductsCommand([Row(1)]), currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeFalse();
        await categoryRepo.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_EmptyRows_Rejected()
    {
        var (currentUser, productRepo, categoryRepo, uow, logger) = BuildMocks();

        var result = await HandleAsync(
            new ImportProductsCommand([]), currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeFalse();
    }

    // ── Picture re-linking (the incident) ───────────────────────────────

    [Fact]
    public async Task Create_SpacedPictureName_LinksTheSanitizedDiskName()
    {
        // THE production incident: the photo was picked as
        // "Pizza flour 500g.png"; the bulk upload stored it on disk as
        // "Pizzaflour500g.png" (allowlist strips the spaces). The import
        // MUST link the sanitized name — the verbatim cell can never
        // resolve.
        var (currentUser, productRepo, categoryRepo, uow, logger) = BuildMocks();

        var result = await HandleAsync(
            new ImportProductsCommand([Row(1, pictureFileName: "Pizza flour 500g.png")]),
            currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeTrue();
        result.Value.CreatedCount.Should().Be(1);
        AddedProducts(productRepo).Should().ContainSingle()
            .Which.PictureUrl.Should().Be("/uploads/products/Pizzaflour500g.png");
    }

    [Fact]
    public async Task Create_SpecialCharacterPictureName_LinksTheSanitizedDiskName()
    {
        // A real cell from the production CSV — double space AND an
        // ampersand, both stripped by the upload's allowlist.
        var (currentUser, productRepo, categoryRepo, uow, logger) = BuildMocks();

        var result = await HandleAsync(
            new ImportProductsCommand(
                [Row(1, name: "Spaghetti with vitamin D and calcium", pictureFileName: "calcium  & Vitamins.png")]),
            currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeTrue();
        AddedProducts(productRepo).Should().ContainSingle()
            .Which.PictureUrl.Should().Be("/uploads/products/calciumVitamins.png");
    }

    [Fact]
    public async Task Create_AlreadyCanonicalPictureName_LinksWithoutChange()
    {
        // "ABC.png" is allowlist-clean — the one product that DID display
        // during the incident. Its URL must be exactly what it always was.
        var (currentUser, productRepo, categoryRepo, uow, logger) = BuildMocks();

        var result = await HandleAsync(
            new ImportProductsCommand([Row(1, name: "Alphabet 500g", pictureFileName: "ABC.png")]),
            currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeTrue();
        AddedProducts(productRepo).Should().ContainSingle()
            .Which.PictureUrl.Should().Be("/uploads/products/ABC.png");
    }

    [Fact]
    public async Task Create_JpegAliasPictureName_LinksSnifferCanonicalExtension()
    {
        // A genuine JPEG named "My photo.jpeg" is stored by the upload as
        // "Myphoto.jpg" (extension forced from the SNIFFED type) — the
        // import's canonicalization must produce the same name.
        var (currentUser, productRepo, categoryRepo, uow, logger) = BuildMocks();

        var result = await HandleAsync(
            new ImportProductsCommand([Row(1, pictureFileName: "My photo.jpeg")]),
            currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeTrue();
        AddedProducts(productRepo).Should().ContainSingle()
            .Which.PictureUrl.Should().Be("/uploads/products/Myphoto.jpg");
    }

    [Fact]
    public async Task Create_EmptyPictureCell_LinksNoPicture()
    {
        var (currentUser, productRepo, categoryRepo, uow, logger) = BuildMocks();

        var result = await HandleAsync(
            new ImportProductsCommand([Row(1, pictureFileName: "   ")]),
            currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeTrue();
        AddedProducts(productRepo).Should().ContainSingle()
            .Which.PictureUrl.Should().BeNull();
    }

    [Fact]
    public async Task Create_UnusablePictureName_LinksNoPicture_RowStillSucceeds()
    {
        // "(((.png" has no allowlist characters — the upload would have
        // REJECTED that file, so there is nothing on disk to link. The row
        // itself is still valid and must import (best-effort, per-row).
        var (currentUser, productRepo, categoryRepo, uow, logger) = BuildMocks();

        var result = await HandleAsync(
            new ImportProductsCommand([Row(1, pictureFileName: "(((.png")]),
            currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeTrue();
        result.Value.FailedCount.Should().Be(0);
        AddedProducts(productRepo).Should().ContainSingle()
            .Which.PictureUrl.Should().BeNull();
    }

    [Fact]
    public async Task Update_ExistingProductWithBrokenVerbatimUrl_ReLinksToSanitizedUrl()
    {
        // The remediation path for the already-imported catalog: the
        // product exists with the broken verbatim URL from before the
        // fix; re-importing the SAME CSV row must rewrite the URL to the
        // sanitized name (and preserve the description).
        var existing = Product.Create(
            "Pizza flour 500g",
            "Existing description", // must survive the update (no CSV column)
            new Money(700_000m, "IRR"),
            5,
            TestValues.CategoryId,
            "/uploads/products/Pizza flour 500g.png"); // the broken verbatim URL

        var (currentUser, productRepo, categoryRepo, uow, logger) =
            BuildMocks(existingProducts: [existing]);

        var result = await HandleAsync(
            new ImportProductsCommand([Row(1, price: 760_000m, stock: 0)]),
            currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeTrue();
        result.Value.UpdatedCount.Should().Be(1);
        result.Value.CreatedCount.Should().Be(0);
        await productRepo.DidNotReceiveWithAnyArgs().AddAsync(default!, default);

        existing.PictureUrl.Should().Be("/uploads/products/Pizzaflour500g.png");
        existing.Description.Should().Be("Existing description");
        existing.Price.Amount.Should().Be(760_000m);
    }

    // ── Category resolution (behavior pin) ──────────────────────────────

    [Fact]
    public async Task Create_UnknownCategory_RowFailsOthersContinue()
    {
        var (currentUser, productRepo, categoryRepo, uow, logger) = BuildMocks();

        var result = await HandleAsync(
            new ImportProductsCommand(
            [
                Row(1, category: "Nonexistent"),
                Row(2, name: "Bread flour 500g", pictureFileName: "Bread flour 500g.png")
            ]),
            currentUser, productRepo, categoryRepo, uow, logger);

        result.IsSuccess.Should().BeTrue();
        result.Value.FailedCount.Should().Be(1);
        result.Value.CreatedCount.Should().Be(1);
        result.Value.RowResults.Should().Contain(r =>
            r.Outcome == ProductImportRowOutcome.Failed &&
            r.Message.Contains("Nonexistent", StringComparison.Ordinal));
        AddedProducts(productRepo).Should().ContainSingle()
            .Which.PictureUrl.Should().Be("/uploads/products/Breadflour500g.png");
    }
}

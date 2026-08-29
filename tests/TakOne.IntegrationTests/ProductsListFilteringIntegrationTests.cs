using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TakOne.Application.Products.Queries.GetProductsPaginated;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.ValueObjects;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the products list's Round 6 server-side filters +
/// sorts: every new WHERE/ORDER BY clause must translate to real SQL and
/// behave correctly against a live EF provider (SQLite here — the same
/// provider the rest of the integration suite uses; production runs SQL
/// Server). Mirrors <see cref="UsersListFilteringIntegrationTests"/> from
/// Round 5 and <see cref="ProductSortIntegrationTests"/> from Round 4.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY REAL-DB TESTS (vs. the handler unit tests)</b>: the unit suite
/// captures the <see cref="ProductsListFilters"/> record against
/// substitutes, which proves the WIRING but not the TRANSLATION. A clause
/// like <c>p.Name.ToLower().Contains(term)</c>, an int-vs-decimal stock
/// comparison, or <c>ORDER BY p.StockQuantity, p.Name</c> could be wired
/// perfectly and still blow up as <c>InvalidOperationException: The LINQ
/// expression could not be translated</c> at runtime. These tests are the
/// contract that keeps the SQL translation honest.
/// </para>
/// <para>
/// <b>SEEDING</b>: products are built via <see cref="Product.Create"/>
/// under real <see cref="Category"/> aggregates (the FK requires the
/// category row to exist; the sub/subsub hierarchy is seeded on the same
/// aggregate before the single SaveChanges, which persists the whole
/// graph). No reflection is needed — every filter-relevant member (Name,
/// Price, StockQuantity, CategoryId, SubCategoryId, SubSubCategoryId) is
/// a factory parameter.
/// </para>
/// <para>
/// <b>CATEGORY-NAME FILTERS</b>: the repository receives the RESOLVED Id
/// sets (the handler resolves the name term against the category tree);
/// these tests hand the Id sets directly, pinning the SQL half of the
/// contract (the in-memory resolution half is pinned by the handler unit
/// tests).
/// </para>
/// </remarks>
public class ProductsListFilteringIntegrationTests
{
    private static async Task<(ProductRepository repo, ApplicationDbContext db)> CreateSeededAsync(
        params Product[] products)
        => await CreateSeededAsync(products, Array.Empty<Category>());

    private static async Task<(ProductRepository repo, ApplicationDbContext db)> CreateSeededAsync(
        Product[] products,
        params Category[] categories)
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var repo = new ProductRepository(db);

        // Categories FIRST: Product.CategoryId has an FK to Categories —
        // a product with a CategoryId whose row doesn't exist fails
        // SaveChanges. The sub/subsub hierarchy is part of the aggregate
        // graph, so one Add + one SaveChanges persists the whole tree.
        foreach (var category in categories)
        {
            db.Categories.Add(category);
        }

        foreach (var product in products)
        {
            await repo.AddAsync(product, CancellationToken.None);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        return (repo, db);
    }

    /// <summary>
    /// Builds the filters record with every member defaulted to "no
    /// filter" — the same shape the handler packs (and the reason the
    /// positional record has no parameter defaults: callers state what
    /// they set, mirroring UsersListFilters).
    /// </summary>
    private static ProductsListFilters Filters(
        string? searchTerm = null,
        ProductsTextFilter? name = null,
        ProductStockStatus? stockStatus = null,
        ProductsNumberFilter? price = null,
        ProductsNumberFilter? stock = null,
        IReadOnlyCollection<Guid>? categoryIds = null,
        IReadOnlyCollection<Guid>? subCategoryIds = null,
        IReadOnlyCollection<Guid>? subSubCategoryIds = null,
        ProductSortBy? sortBy = null,
        bool sortDescending = false)
        => new(
            SearchTerm: searchTerm,
            CategoryId: null,
            SubCategoryId: null,
            SubSubCategoryId: null,
            Name: name,
            StockStatus: stockStatus,
            Price: price,
            Stock: stock,
            CategoryIds: categoryIds,
            SubCategoryIds: subCategoryIds,
            SubSubCategoryIds: subSubCategoryIds,
            SortBy: sortBy,
            SortDescending: sortDescending);

    /// <summary>
    /// Creates ONE category and returns it with its products seeded under
    /// it — the minimal setup for the column-filter tests that don't care
    /// about categories.
    /// </summary>
    private static async Task<(ProductRepository repo, ApplicationDbContext db, Guid categoryId)>
        CreateSeededWithCategoryAsync(params Product[] products)
    {
        var category = Category.Create("Filter Test Category");
        var (repo, db) = await CreateSeededAsync(products, category);
        return (repo, db, category.Id);
    }

    private static Product MakeProduct(
        string name,
        decimal price = 10m,
        int stockQuantity = 100,
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null)
        => Product.Create(
            name,
            description: $"Test product {name}",
            price: new Money(price, "IRR"),
            stockQuantity: stockQuantity,
            categoryId: categoryId ?? Guid.NewGuid(),
            subCategoryId: subCategoryId,
            subSubCategoryId: subSubCategoryId);

    // ── Default order (Name asc) — the contract existing callers rely on

    [Fact]
    public async Task GetPaginatedAsync_NoFilters_DefaultsToNameAscending()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Cherry"),
            MakeProduct("Apple"),
            MakeProduct("Banana"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(Filters(), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.Items.Select(p => p.Name)
                .Should().BeInAscendingOrder("Name asc is the pre-Round-4 default the shop relies on");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_NullFilters_DefaultsToNameAscending()
    {
        // The handler may hand a null filters record (defensive default).
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Banana"),
            MakeProduct("Apple"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(null, pageNumber: 1, pageSize: 10);

            result.Items.Select(p => p.Name).Should().BeInAscendingOrder();
        }
    }

    // ── Sorting (Round 6 additions) ─────────────────────────────────────

    [Fact]
    public async Task GetPaginatedAsync_SortByNameDescending_OrdersInSql()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Alice"),
            MakeProduct("Carol"),
            MakeProduct("Bob"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(sortBy: ProductSortBy.Name, sortDescending: true),
                pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.Items.Select(p => p.Name)
                .Should().BeInDescendingOrder("the grid's Name-header descending click");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_SortByStock_OrdersInSqlBothDirections()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Many", stockQuantity: 50),
            MakeProduct("Few", stockQuantity: 2),
            MakeProduct("None", stockQuantity: 0),
            MakeProduct("Some", stockQuantity: 10));

        await using (db)
        {
            var asc = await repo.GetPaginatedAsync(
                Filters(sortBy: ProductSortBy.StockLowToHigh), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);
            var desc = await repo.GetPaginatedAsync(
                Filters(sortBy: ProductSortBy.StockHighToLow), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            asc.Items.Select(p => p.StockQuantity).Should().BeInAscendingOrder();
            desc.Items.Select(p => p.StockQuantity).Should().BeInDescendingOrder();
            asc.Items.Select(p => p.Name)
                .Should().Equal(new[] { "None", "Few", "Some", "Many" });
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_PriceSortDescendingFlag_OrdersInSql()
    {
        // The (PriceLowToHigh, descending) combination is a Round-6 arm
        // only the admin grid can reach — the translator maps a
        // descending Price-header click to PriceHighToLow, but a malformed
        // caller could send the flag, and the switch must still produce a
        // defined order.
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Expensive", price: 500m),
            MakeProduct("Cheap", price: 5m),
            MakeProduct("Middle", price: 50m));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(sortBy: ProductSortBy.PriceLowToHigh, sortDescending: true),
                pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.Items.Select(p => p.Name)
                .Should().ContainInOrder("Expensive", "Middle", "Cheap");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_EqualStockKeys_TiebreakByNameAcrossPages()
    {
        // SIX products at the SAME stock; page size 2 → three pages. The
        // NAME tiebreaker must keep the union of pages equal to the
        // name-ascending order (no skips, no duplicates) — deterministic
        // OFFSET/FETCH paging (names are unique, so the tiebreaker is
        // total).
        var products = new[]
        {
            MakeProduct("Delta"), MakeProduct("Alpha"), MakeProduct("Echo"),
            MakeProduct("Bravo"), MakeProduct("Foxtrot"), MakeProduct("Charlie")
        };

        var (repo, db) = await CreateSeededAsync(products);
        await using (db)
        {
            var all = new List<string>();
            for (var page = 1; page <= 3; page++)
            {
                var result = await repo.GetPaginatedAsync(
                    Filters(sortBy: ProductSortBy.StockLowToHigh), pageNumber: page, pageSize: 2, cancellationToken: CancellationToken.None);
                result.TotalCount.Should().Be(6);
                all.AddRange(result.Items.Select(p => p.Name));
            }

            all.Should().BeEquivalentTo(new[]
            {
                "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot"
            });
            all.Should().BeInAscendingOrder(
                "the name tiebreaker keeps equal-stock products deterministic across pages");
        }
    }

    // ── Stock-status filter (the Status column's dropdown) ─────────────

    [Fact]
    public async Task GetPaginatedAsync_StockStatusFilter_FiltersInSql()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("In1", stockQuantity: 5),
            MakeProduct("Out1", stockQuantity: 0),
            MakeProduct("In2", stockQuantity: 50),
            MakeProduct("Out2", stockQuantity: 0));

        await using (db)
        {
            var inStock = await repo.GetPaginatedAsync(
                Filters(stockStatus: ProductStockStatus.InStock), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);
            var outOfStock = await repo.GetPaginatedAsync(
                Filters(stockStatus: ProductStockStatus.OutOfStock), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            inStock.TotalCount.Should().Be(2);
            inStock.Items.Should().OnlyContain(p => p.StockQuantity > 0);
            outOfStock.TotalCount.Should().Be(2);
            outOfStock.Items.Should().OnlyContain(p => p.StockQuantity == 0);
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_StockStatusComposesWithStockNumberFilter()
    {
        // Both StockQuantity columns can carry a filter at once (the
        // status dropdown's key AND the stock column's numeric filter) —
        // they AND in SQL. A user asking "out of stock AND stock < 5"
        // gets exactly the zero-stock rows (0 < 5); "in stock AND
        // stock <= 5" gets the low-stock rows.
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Zero", stockQuantity: 0),
            MakeProduct("Low", stockQuantity: 3),
            MakeProduct("High", stockQuantity: 99));

        await using (db)
        {
            var outAndLow = await repo.GetPaginatedAsync(
                Filters(
                    stockStatus: ProductStockStatus.OutOfStock,
                    stock: new ProductsNumberFilter(ProductsNumberOperator.LessThan, 5m)),
                pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);
            var inAndLow = await repo.GetPaginatedAsync(
                Filters(
                    stockStatus: ProductStockStatus.InStock,
                    stock: new ProductsNumberFilter(ProductsNumberOperator.LessThanOrEqual, 5m)),
                pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            outAndLow.Items.Select(p => p.Name).Should().Equal("Zero");
            inAndLow.Items.Select(p => p.Name).Should().Equal("Low");
        }
    }

    // ── Name text filter (LOWER()/LIKE translation) ────────────────────

    [Theory]
    [InlineData(ProductsTextOperator.Contains, "wid", true)]
    [InlineData(ProductsTextOperator.Contains, "xyz", false)]
    [InlineData(ProductsTextOperator.NotContains, "wid", false)]
    [InlineData(ProductsTextOperator.Equals, "super widget", true)]
    [InlineData(ProductsTextOperator.Equals, "SUPER WIDGET", true)]
    [InlineData(ProductsTextOperator.NotEquals, "super widget", false)]
    [InlineData(ProductsTextOperator.StartsWith, "super", true)]
    [InlineData(ProductsTextOperator.StartsWith, "widget", false)]
    [InlineData(ProductsTextOperator.EndsWith, "widget", true)]
    [InlineData(ProductsTextOperator.EndsWith, "super", false)]
    public async Task GetPaginatedAsync_NameTextFilter_AllOperatorsTranslate(
        ProductsTextOperator op, string value, bool expectedWidgetMatch)
    {
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Super Widget"),
            MakeProduct("Boring Bolt"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(name: new ProductsTextFilter(value, op)), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            // Whether the Widget matches under the operator (Bolt's
            // membership varies per operator: NotContains/NotEquals keep
            // him, the positive forms exclude him — asserting the
            // Widget's presence/absence is the operator's contract).
            var widgetMatched = result.Items.Any(p => p.Name == "Super Widget");
            widgetMatched.Should().Be(expectedWidgetMatch,
                $"{op} '{value}' against Super Widget (case-insensitive in SQL)");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_NameTextFilterWhitespaceValue_NoClause()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Alpha"),
            MakeProduct("Beta"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(name: new ProductsTextFilter("   ", ProductsTextOperator.Contains)),
                pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.TotalCount.Should().Be(2, "a whitespace-only filter value adds no WHERE clause");
        }
    }

    // ── SearchTerm (the legacy shop/mobile filter — now genuinely
    //    case-insensitive on BOTH providers via LOWER()) ────────────────

    [Fact]
    public async Task GetPaginatedAsync_SearchTerm_MatchesNameCaseInsensitively()
    {
        // SQLite's default collation is case-SENSITIVE — the pre-Round-6
        // bare Contains did NOT match "WIDGET" against "Super Widget"
        // there. The LOWER() rewrite makes it genuinely case-insensitive.
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Super Widget"),
            MakeProduct("Boring Bolt"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(searchTerm: "WIDGET"), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.TotalCount.Should().Be(1);
            result.Items.Should().ContainSingle(p => p.Name == "Super Widget");
        }
    }

    // ── Numeric column filters (Price + Stock) ─────────────────────────

    // NOTE: decimal is not a legal attribute-constant type in C#, so
    // the theory carries ints and the test converts to decimal.
    [Theory]
    [InlineData(ProductsNumberOperator.GreaterThan, 100, new int[] { 150, 500 })]
    [InlineData(ProductsNumberOperator.GreaterThanOrEqual, 100, new int[] { 100, 150, 500 })]
    [InlineData(ProductsNumberOperator.LessThan, 100, new int[] { 50 })]
    [InlineData(ProductsNumberOperator.LessThanOrEqual, 100, new int[] { 50, 100 })]
    [InlineData(ProductsNumberOperator.Equals, 100, new int[] { 100 })]
    [InlineData(ProductsNumberOperator.NotEquals, 100, new int[] { 50, 150, 500 })]
    public async Task GetPaginatedAsync_PriceFilter_AllOperatorsTranslate(
        ProductsNumberOperator op, int operand, int[] expectedPrices)
    {
        var (repo, db, _) = await CreateSeededWithCategoryAsync(
            MakeProduct("A", price: 50m),
            MakeProduct("B", price: 100m),
            MakeProduct("C", price: 150m),
            MakeProduct("D", price: 500m));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(price: new ProductsNumberFilter(op, operand)), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.Items.Select(p => p.Price.Amount)
                .Should().BeEquivalentTo(expectedPrices.Select(a => (decimal)a));
            result.TotalCount.Should().Be(expectedPrices.Length);
        }
    }

    [Theory]
    [InlineData(ProductsNumberOperator.GreaterThan, 10, new int[] { 50 })]
    [InlineData(ProductsNumberOperator.LessThanOrEqual, 10, new int[] { 0, 10 })]
    [InlineData(ProductsNumberOperator.Equals, 0, new int[] { 0 })]
    [InlineData(ProductsNumberOperator.NotEquals, 50, new int[] { 0, 10 })]
    public async Task GetPaginatedAsync_StockFilter_IntColumnComparesAgainstDecimalOperand(
        ProductsNumberOperator op, int operand, int[] expectedStocks)
    {
        // The typed record stores a decimal operand; the Stock column is
        // an int. This pins that the int-vs-decimal comparison translates
        // (EF casts the parameter to the column's store type) instead of
        // throwing at runtime.
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("A", stockQuantity: 0),
            MakeProduct("B", stockQuantity: 10),
            MakeProduct("C", stockQuantity: 50));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(stock: new ProductsNumberFilter(op, operand)), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.Items.Select(p => p.StockQuantity)
                .Should().BeEquivalentTo(expectedStocks);
            result.TotalCount.Should().Be(expectedStocks.Length);
        }
    }

    // ── Category-name filters (the resolved Id sets) ───────────────────

    [Fact]
    public async Task GetPaginatedAsync_CategoryIdsFilter_FiltersInSql()
    {
        var electronics = Category.Create("Electronics");
        var groceries = Category.Create("Groceries");
        var (repo, db) = await CreateSeededAsync(
            new[]
            {
                MakeProduct("Phone", categoryId: electronics.Id),
                MakeProduct("Laptop", categoryId: electronics.Id),
                MakeProduct("Milk", categoryId: groceries.Id)
            },
            electronics, groceries);

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(categoryIds: new[] { electronics.Id }), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.TotalCount.Should().Be(2);
            result.Items.Should().OnlyContain(p => p.CategoryId == electronics.Id);
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_EmptyCategoryIdsSet_MatchesNothing()
    {
        // The handler resolves a name term that matches NO category to an
        // EMPTY set — "no category matches" must mean zero rows, not "no
        // filter" (which would silently show everything). This pins EF's
        // translation of an empty-collection Contains as a
        // matches-nothing predicate.
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Phone"),
            MakeProduct("Milk"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(categoryIds: Array.Empty<Guid>()), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.TotalCount.Should().Be(0, "an empty resolved set means the name term matched no category");
            result.Items.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_SubCategoryIdsFilter_NullSubCategoryNeverMatches()
    {
        var electronics = Category.Create("Electronics");
        var laptops = electronics.AddSubCategory("Laptops");
        var (repo, db) = await CreateSeededAsync(
            new[]
            {
                MakeProduct("ThinkPad", categoryId: electronics.Id, subCategoryId: laptops.Id),
                MakeProduct("Generic Gadget", categoryId: electronics.Id) // no sub-category
            },
            electronics);

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(subCategoryIds: new[] { laptops.Id }), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.TotalCount.Should().Be(1);
            result.Items.Should().ContainSingle(p => p.Name == "ThinkPad",
                "products with NO sub-category render the '—' placeholder — a name term can't match them");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_SubSubCategoryIdsFilter_FiltersInSql()
    {
        var electronics = Category.Create("Electronics");
        var laptops = electronics.AddSubCategory("Laptops");
        electronics.AddSubSubCategory(laptops.Id, "Gaming Laptops");
        var gamingId = laptops.SubSubCategories.Single().Id;

        var (repo, db) = await CreateSeededAsync(
            new[]
            {
                MakeProduct("Gamer Rig", categoryId: electronics.Id,
                    subCategoryId: laptops.Id, subSubCategoryId: gamingId),
                MakeProduct("Office Rig", categoryId: electronics.Id, subCategoryId: laptops.Id)
            },
            electronics);

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(subSubCategoryIds: new[] { gamingId }), pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.TotalCount.Should().Be(1);
            result.Items.Should().ContainSingle(p => p.Name == "Gamer Rig");
        }
    }

    // ── Composition + paging ────────────────────────────────────────────

    [Fact]
    public async Task GetPaginatedAsync_FiltersSortAndPaging_TotalCountStaysAccurate()
    {
        // 12 in-stock "Widget ..." products priced 10..21 + 8 zero-stock
        // "Widget ..." products + 5 unrelated products — a name filter +
        // stock-status + price filter compose in SQL, sort by stock
        // descending, and the TotalCount describes the FILTERED set, not
        // the page.
        var products = new List<Product>();
        for (var i = 1; i <= 12; i++)
        {
            products.Add(MakeProduct($"Widget {i:00}", price: 10m + i, stockQuantity: i));
        }
        for (var i = 13; i <= 20; i++)
        {
            products.Add(MakeProduct($"Widget {i:00}", price: 100m, stockQuantity: 0));
        }
        for (var i = 1; i <= 5; i++)
        {
            products.Add(MakeProduct($"Gadget {i:00}"));
        }

        var (repo, db) = await CreateSeededAsync(products.ToArray());
        await using (db)
        {
            var page1 = await repo.GetPaginatedAsync(
                Filters(
                    name: new ProductsTextFilter("widget", ProductsTextOperator.Contains),
                    stockStatus: ProductStockStatus.InStock,
                    price: new ProductsNumberFilter(ProductsNumberOperator.LessThanOrEqual, 20m),
                    sortBy: ProductSortBy.StockHighToLow),
                pageNumber: 1, pageSize: 4, cancellationToken: CancellationToken.None);

            // In-stock widgets priced <= 20: prices 11..20 → 10 products
            // (price = 10 + i, i = 1..12).
            page1.TotalCount.Should().Be(10, "the count describes the whole filtered set, not the page");
            page1.Items.Should().HaveCount(4, "page size 4");
            page1.Items.Select(p => p.StockQuantity).Should().BeInDescendingOrder();

            var page3 = await repo.GetPaginatedAsync(
                Filters(
                    name: new ProductsTextFilter("widget", ProductsTextOperator.Contains),
                    stockStatus: ProductStockStatus.InStock,
                    price: new ProductsNumberFilter(ProductsNumberOperator.LessThanOrEqual, 20m),
                    sortBy: ProductSortBy.StockHighToLow),
                pageNumber: 3, pageSize: 4, cancellationToken: CancellationToken.None);
            page3.Items.Should().HaveCount(2, "the remaining rows of the filtered set");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_SearchTermComposesWithNameFilter()
    {
        var (repo, db) = await CreateSeededAsync(
            MakeProduct("Widget Pro"),
            MakeProduct("Widget Lite"),
            MakeProduct("Bolt"));

        await using (db)
        {
            var result = await repo.GetPaginatedAsync(
                Filters(
                    searchTerm: "widget",
                    name: new ProductsTextFilter("pro", ProductsTextOperator.Contains)),
                pageNumber: 1, pageSize: 10, cancellationToken: CancellationToken.None);

            result.Items.Select(p => p.Name)
                .Should().Equal(new[] { "Widget Pro" }, "searchTerm AND the column filter compose in SQL");
        }
    }

    [Fact]
    public async Task GetPaginatedAsync_EmptySeedSet_ReturnsEmptyPage()
    {
        var (repo, db) = await CreateSeededAsync();
        await using (db)
        {
            var result = await repo.GetPaginatedAsync(Filters(), pageNumber: 1, pageSize: 20, cancellationToken: CancellationToken.None);

            result.TotalCount.Should().Be(0);
            result.Items.Should().BeEmpty();
        }
    }
}

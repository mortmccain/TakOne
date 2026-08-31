using TakOne.Application.Products.Queries.GetProductsPaginated;
using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IProductRepository"/>.
///
/// TRACKING POLICY:
///   Read methods (<see cref="GetByIdAsync"/>, <see cref="GetPaginatedAsync"/>)
///   return TRACKED entities — i.e. NO <c>AsNoTracking()</c>. This is deliberate
///   because the application-layer command handlers load a Product, call domain
///   methods on it (e.g. <c>IncreaseStock</c>, <c>UpdateDetails</c>), and then
///   call <c>IUnitOfWork.SaveChangesAsync</c>. EF Core's change tracker detects
///   the mutations and generates UPDATE statements automatically. If we read
///   with <c>AsNoTracking</c>, the handler's mutations would be silently
///   discarded on SaveChanges.
///
///   For pure read-only queries (e.g. <see cref="ExistsAsync"/>,
///   <see cref="NameExistsAsync"/>), tracking doesn't matter — they return
///   scalars, not entities — so we skip the decision entirely.
///
/// VALUE OBJECTS AUTO-LOADED:
///   <see cref="Product.Price"/> (Money, ComplexProperty) and
///   <see cref="Product.PurchaseLimits"/> (CustomerGroupPurchaseLimit, OwnsMany)
///   are part of the Product aggregate's table layout. EF Core loads them
///   automatically whenever a Product is materialized — no <c>.Include()</c>
///   needed. This is the difference between COMPLEX/OWNED types (always
///   loaded with their owner) and NAVIGATION properties (separate entity,
///   lazy/explicit load required).
///
/// SEARCH-TERM SEMANTICS (Round 6 — aligned with the users/sales lists):
///   <c>searchTerm</c> (and the Name column filter) match
///   <see cref="Product.Name"/> with a case-insensitive <c>Contains</c>,
///   implemented as <c>p.Name.ToLower().Contains(term.ToLowerInvariant())</c>
///   — the overload pair EF Core translates to <c>LOWER() LIKE '%term%'</c>
///   on BOTH providers. The pre-Round-6 bare <c>Contains</c> relied on SQL
///   Server's default case-insensitive collation and was NOT actually
///   case-insensitive on SQLite (whose default collation is case-SENSITIVE) —
///   the same latent defect the Round-5 users conversion fixed. The
///   culture-taking overloads the analyzers prefer are NOT EF-translatable,
///   hence the pragma-guarded region below (same rationale as
///   <c>UserRepository</c> / <c>SalesSpecificationFilters</c>).
/// </summary>
public sealed class ProductRepository : IProductRepository
{
    private readonly ApplicationDbContext _db;

    public ProductRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Product?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // FindAsync uses the PK and consults the change tracker first — if a
        // previous operation in this UoW already loaded the same Product,
        // FindAsync returns the tracked instance without hitting the DB.
        // Price + PurchaseLimits auto-load (owned).
        return await _db.Products.FindAsync(new object[] { id }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<Product>> GetByIdsAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Materialize the input so we don't enumerate twice (once for the
        // empty check, once for the Where clause) — important if the caller
        // passes a lazy IEnumerable that does I/O on each enumeration.
        // ------------------------------------------------------------------
        var idList = ids as IList<Guid> ?? ids.ToList();

        if (idList.Count == 0)
        {
            return new List<Product>();
        }

        // ------------------------------------------------------------------
        // Single round-trip: SELECT * FROM Products WHERE Id IN (@id1, @id2, ...)
        // EF Core translates .Where(p => idList.Contains(p.Id)) into an IN clause.
        //
        // We DON'T use FindAsync here because FindAsync only takes a single PK
        // (no batch overload). The trade-off: we lose the change-tracker cache
        // lookup that FindAsync provides. For typical batch callers (cart
        // enrichment, sales-list projection), the entities being loaded aren't
        // already tracked by the caller's DbContext scope anyway, so the cache
        // wouldn't help. If the caller IS in a tracking context and wants to
        // consult the cache, they should call GetByIdAsync per id instead.
        //
        // Price + PurchaseLimits auto-load (owned) — no .Include() needed.
        // ------------------------------------------------------------------
        return await _db.Products
            .Where(p => idList.Contains(p.Id))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<Product>> GetByIdsReadOnlyAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // READ-ONLY batch load — AsNoTracking variant of GetByIdsAsync.
        // Used by QuickReorderLastSaleCommandHandler to avoid the EF Core
        // owned-Money tracking conflict (see IProductRepository.GetByIdsReadOnlyAsync
        // XML doc for the full rationale).
        //
        // AsNoTracking materializes entities WITHOUT adding them to the change
        // tracker. The owned Money Price and the owned PurchaseLimits
        // collection are also materialized without tracking.
        // ------------------------------------------------------------------
        var idList = ids as IList<Guid> ?? ids.ToList();

        if (idList.Count == 0)
        {
            return new List<Product>();
        }

        return await _db.Products
            .AsNoTracking()
            .Where(p => idList.Contains(p.Id))
            .ToListAsync(cancellationToken);
    }

    // ── Round 6: server-side text filters ──────────────────────────────
    //
    // The string predicates inside GetPaginatedAsync (both in the
    // search-term lambda and in the Name column filter's operator switch)
    // use the parameterless string.ToLower()/Contains(string) deliberately:
    // those are the overloads EF Core translates to LOWER()/LIKE. The
    // culture-taking overloads the analyzers prefer are NOT translatable
    // (same rationale as UserRepository / SalesSpecificationFilters).
#pragma warning disable CA1304 // ToLower culture — SQL LOWER() has no culture
#pragma warning disable CA1311 // Contains culture — same
#pragma warning disable CA1862 // OrdinalIgnoreCase overload — not EF-translatable

    /// <inheritdoc />
    public async Task<PaginatedResult<Product>> GetPaginatedAsync(
        ProductsListFilters? filters = null,
        ProductVisibilityFilter? visibility = null,
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Defensive: clamp page parameters. The handler also clamps, but a
        // future caller might call the repository directly. Never trust the
        // caller with pagination — bad values produce nonsensical SQL
        // (OFFSET -1) or memory pressure (pageSize = int.MaxValue).
        // ------------------------------------------------------------------
        pageNumber = pageNumber < 1 ? 1 : pageNumber;
        pageSize = pageSize < 1 ? 20 : pageSize;

        filters ??= new ProductsListFilters(
            SearchTerm: null,
            CategoryId: null,
            SubCategoryId: null,
            SubSubCategoryId: null,
            Name: null,
            StockStatus: null,
            Price: null,
            Stock: null,
            CategoryIds: null,
            SubCategoryIds: null,
            SubSubCategoryIds: null,
            SortBy: null,
            SortDescending: false);

        // ------------------------------------------------------------------
        // Build the filter query. We use IQueryable so that all Where clauses
        // are composed into a SINGLE SQL statement (rather than fetching all
        // rows then filtering in memory). Each conditional Where is a no-op
        // when the filter value is null.
        // ------------------------------------------------------------------
        var query = _db.Products.AsQueryable();

        // ── Legacy exact-match category filters (the shop's category
        //    navigation; a CategoryId filter returns products in that
        //    category OR any of its sub/subsub categories via the
        //    hierarchy semantics documented on the query). ──
        if (filters.CategoryId is not null)
        {
            query = query.Where(p => p.CategoryId == filters.CategoryId.Value);
        }

        if (filters.SubCategoryId is not null)
        {
            query = query.Where(p => p.SubCategoryId == filters.SubCategoryId.Value);
        }

        if (filters.SubSubCategoryId is not null)
        {
            query = query.Where(p => p.SubSubCategoryId == filters.SubSubCategoryId.Value);
        }

        // searchTerm matches Product.Name (case-insensitive Contains).
        // We trim and skip empty/null so an empty search box doesn't accidentally
        // filter out products whose names are null (shouldn't happen, but cheap
        // to guard).
        //
        // Round 6: the match is now genuinely case-insensitive on BOTH
        // providers — SQLite's default collation is case-SENSITIVE, so the
        // pre-Round-6 bare Contains was not actually case-insensitive there
        // (same fix the Round-5 users list applied to its search term).
        var trimmedSearch = filters.SearchTerm?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedSearch))
        {
            var searchLower = trimmedSearch.ToLowerInvariant();
            query = query.Where(p => p.Name.ToLower().Contains(searchLower));
        }

        // ------------------------------------------------------------------
        // CUSTOMER-VISIBILITY FILTER (in-stock + active category hierarchy).
        //
        // These predicates MUST run inside the SQL query — NOT in the
        // handler after pagination — otherwise the DB pages the FULL
        // catalog and the handler then strips rows from the already-paged
        // slice: pages come back partially empty (12 of 20 slots), and
        // TotalCount includes items the customer can never see, breaking
        // the pager math. EF Core translates Contains over a Guid
        // collection into an IN (...) clause.
        //
        // Null id-sets mean "category state unknown" (e.g. the handler's
        // category-tree load failed) — that level's predicate is skipped so
        // the catalog degrades to in-stock-only rather than empty. An
        // EMPTY (non-null) set means "everything at this level is
        // deactivated" and filters all products referencing the level.
        // ------------------------------------------------------------------
        if (visibility is not null)
        {
            // Customer catalog hides zero-stock products.
            query = query.Where(p => p.StockQuantity > 0);

            if (visibility.ActiveCategoryIds is { } activeCategoryIds)
            {
                query = query.Where(p => activeCategoryIds.Contains(p.CategoryId));
            }

            if (visibility.ActiveSubCategoryIds is { } activeSubCategoryIds)
            {
                query = query.Where(p =>
                    p.SubCategoryId == null || activeSubCategoryIds.Contains(p.SubCategoryId.Value));
            }

            if (visibility.ActiveSubSubCategoryIds is { } activeSubSubCategoryIds)
            {
                query = query.Where(p =>
                    p.SubSubCategoryId == null || activeSubSubCategoryIds.Contains(p.SubSubCategoryId.Value));
            }
        }

        // ── Round 6: the AdminProducts grid's typed column filters. ──
        //
        // Typed per-column text filter on Name. Written as a plain
        // operator switch over lambdas (NOT the hand-built expression
        // trees the users/sales lists use) because the products list has
        // exactly ONE text column on the Product table — the expression-
        // tree machinery earns its keep only when several columns share
        // one ApplyTextFilter helper. Each arm is a simple MemberAccess →
        // method-call chain that EF translates to LOWER()/LIKE on both
        // providers.
        var nameFilter = filters.Name;
        var nameTerm = nameFilter?.Value?.Trim();
        if (nameFilter is not null && !string.IsNullOrEmpty(nameTerm))
        {
            var loweredName = nameTerm.ToLowerInvariant();
            query = nameFilter.Operator switch
            {
                ProductsTextOperator.Contains =>
                    query.Where(p => p.Name.ToLower().Contains(loweredName)),
                ProductsTextOperator.NotContains =>
                    query.Where(p => !p.Name.ToLower().Contains(loweredName)),
                ProductsTextOperator.Equals =>
                    query.Where(p => p.Name.ToLower() == loweredName),
                ProductsTextOperator.NotEquals =>
                    query.Where(p => p.Name.ToLower() != loweredName),
                ProductsTextOperator.StartsWith =>
                    query.Where(p => p.Name.ToLower().StartsWith(loweredName)),
                ProductsTextOperator.EndsWith =>
                    query.Where(p => p.Name.ToLower().EndsWith(loweredName)),
                // Unknown operator values (a malformed message could carry
                // an out-of-range enum) are ignored — lenient no-filter.
                _ => query
            };
        }

        // Stock-status dropdown filter (the grid's Status column):
        // In stock = StockQuantity > 0, Out of stock == 0. Composes with
        // the numeric Stock filter below — both reference StockQuantity
        // and are AND-ed.
        if (filters.StockStatus is { } stockStatus)
        {
            query = stockStatus switch
            {
                ProductStockStatus.InStock => query.Where(p => p.StockQuantity > 0),
                ProductStockStatus.OutOfStock => query.Where(p => p.StockQuantity == 0),
                _ => query
            };
        }

        // Numeric comparison filters — Price.Amount (complex property, EF
        // flattens to the Price_Amount column — the same translation the
        // Round-4 price sorts rely on) and StockQuantity. Unknown operator
        // values are ignored (lenient no-filter).
        if (filters.Price is { } price)
        {
            query = price.Operator switch
            {
                ProductsNumberOperator.Equals =>
                    query.Where(p => p.Price.Amount == price.Value),
                ProductsNumberOperator.NotEquals =>
                    query.Where(p => p.Price.Amount != price.Value),
                ProductsNumberOperator.GreaterThan =>
                    query.Where(p => p.Price.Amount > price.Value),
                ProductsNumberOperator.GreaterThanOrEqual =>
                    query.Where(p => p.Price.Amount >= price.Value),
                ProductsNumberOperator.LessThan =>
                    query.Where(p => p.Price.Amount < price.Value),
                ProductsNumberOperator.LessThanOrEqual =>
                    query.Where(p => p.Price.Amount <= price.Value),
                _ => query
            };
        }

        // Numeric comparison filter — StockQuantity. Unknown operator
        // values are ignored (lenient no-filter). See the Price filter
        // above for the shared operator-switch shape.
        //
        // STOCK OPERAND CONVERSION: the typed record carries a decimal (one
        // shape serves both columns), but StockQuantity is an int —
        // comparing an INTEGER column against a decimal parameter is
        // type-mismatched SQL on SQLite (cross-type comparisons are never
        // equal and always ordered) and forces an index-hostile implicit
        // conversion on SQL Server. The operand is converted to the
        // column's CLR type ONCE, client-side, so the comparison is
        // int-vs-int on every provider. Stock counts are integral by
        // domain; a fractional operand (only possible from a stale filter
        // state) truncates toward zero — a documented, harmless
        // degradation.
        if (filters.Stock is { } stock)
        {
            var stockValue = (int)stock.Value;
            query = stock.Operator switch
            {
                ProductsNumberOperator.Equals =>
                    query.Where(p => p.StockQuantity == stockValue),
                ProductsNumberOperator.NotEquals =>
                    query.Where(p => p.StockQuantity != stockValue),
                ProductsNumberOperator.GreaterThan =>
                    query.Where(p => p.StockQuantity > stockValue),
                ProductsNumberOperator.GreaterThanOrEqual =>
                    query.Where(p => p.StockQuantity >= stockValue),
                ProductsNumberOperator.LessThan =>
                    query.Where(p => p.StockQuantity < stockValue),
                ProductsNumberOperator.LessThanOrEqual =>
                    query.Where(p => p.StockQuantity <= stockValue),
                _ => query
            };
        }

        // ------------------------------------------------------------------
        // CATEGORY-NAME FILTERS as resolved Id sets (Round 6).
        //
        // The grid's Category columns filter by free text on the RESOLVED
        // display name, but Product rows store only Guid FKs — the query
        // handler resolves the name term against the category tree and
        // hands us the matched Ids. NULL set = no filter; EMPTY set = "no
        // category matches the term" (EF translates an empty-collection
        // Contains to a matches-nothing predicate, so the filtered result
        // is correctly zero rows).
        //
        // Sub/SubSub levels: products with a NULL reference never match —
        // their cells render the "—" placeholder, which a text term cannot
        // legitimately match. (For the VISIBILITY filter above, nulls PASS;
        // here they are excluded — the two filters answer different
        // questions: "is this product visible to customers?" vs "does this
        // product's category name match the term?")
        // ------------------------------------------------------------------
        if (filters.CategoryIds is { } categoryIds)
        {
            query = query.Where(p => categoryIds.Contains(p.CategoryId));
        }

        if (filters.SubCategoryIds is { } subCategoryIds)
        {
            query = query.Where(p =>
                p.SubCategoryId != null && subCategoryIds.Contains(p.SubCategoryId.Value));
        }

        if (filters.SubSubCategoryIds is { } subSubCategoryIds)
        {
            query = query.Where(p =>
                p.SubSubCategoryId != null && subSubCategoryIds.Contains(p.SubSubCategoryId.Value));
        }

        // ------------------------------------------------------------------
        // Total count — run BEFORE pagination. CountAsync translates to
        // SELECT COUNT(*) FROM Products WHERE ... — fast on indexed columns.
        // ------------------------------------------------------------------
        var totalCount = await query.CountAsync(cancellationToken);

        // ------------------------------------------------------------------
        // Apply ordering + pagination.
        //
        // ORDER BY is REQUIRED before OFFSET/FETCH (SQL Server enforces this).
        // Without it, EF Core throws at runtime.
        //
        // Round 4 — the shop's sort control parameterizes the ORDER BY:
        //   Name (default — the pre-Round-4 hard-coded order, so nothing
        //        shifts for users who never touch the control)
        //   PriceLowToHigh / PriceHighToLow — with the product NAME as a
        //        tiebreaker so equal-priced products page deterministically
        //        (OFFSET/FETCH must never skip or duplicate rows).
        //
        // Round 6 — the AdminProducts grid adds: the Name-DESCENDING
        // variant (via SortDescending) and the Stock orders. The
        // descending variants of the price keys are new (key, direction)
        // combinations only the admin grid can reach; the Round-4 arms
        // existing callers rely on are bit-for-bit unchanged. Every arm
        // carries a total tiebreaker (Id for the name orders — the name
        // is the sort key itself; NAME for the price/stock orders —
        // product names are unique, enforced by NameExistsAsync, so the
        // tiebreaker is total) so OFFSET/FETCH paging stays deterministic.
        // ------------------------------------------------------------------
        var ordered = (filters.SortBy ?? ProductSortBy.Name, filters.SortDescending) switch
        {
            (ProductSortBy.Name, false) =>
                query.OrderBy(p => p.Name).ThenBy(p => p.Id),
            (ProductSortBy.Name, true) =>
                query.OrderByDescending(p => p.Name).ThenByDescending(p => p.Id),

            // Round-4 shop sorts (ascending arms unchanged from Round 4).
            (ProductSortBy.PriceLowToHigh, false) =>
                query.OrderBy(p => p.Price.Amount).ThenBy(p => p.Name),
            (ProductSortBy.PriceLowToHigh, true) =>
                query.OrderByDescending(p => p.Price.Amount).ThenByDescending(p => p.Name),
            (ProductSortBy.PriceHighToLow, false) =>
                query.OrderByDescending(p => p.Price.Amount).ThenBy(p => p.Name),
            (ProductSortBy.PriceHighToLow, true) =>
                query.OrderByDescending(p => p.Price.Amount).ThenByDescending(p => p.Name),

            // Round 6 — the Stock orders.
            (ProductSortBy.StockLowToHigh, false) =>
                query.OrderBy(p => p.StockQuantity).ThenBy(p => p.Name),
            (ProductSortBy.StockLowToHigh, true) =>
                query.OrderByDescending(p => p.StockQuantity).ThenByDescending(p => p.Name),
            (ProductSortBy.StockHighToLow, false) =>
                query.OrderByDescending(p => p.StockQuantity).ThenBy(p => p.Name),
            (ProductSortBy.StockHighToLow, true) =>
                query.OrderByDescending(p => p.StockQuantity).ThenByDescending(p => p.Name),

            _ => query.OrderBy(p => p.Name).ThenBy(p => p.Id)
        };

        var items = await ordered
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<Product>(items, totalCount, pageNumber, pageSize);
    }

#pragma warning restore CA1304
#pragma warning restore CA1311
#pragma warning restore CA1862

    /// <inheritdoc />
    /// <remarks>
    /// READ-ONLY load — returns an <c>AsNoTracking</c> entity. The caller
    /// MUST NOT mutate the returned Product; any mutations would be silently
    /// discarded on SaveChanges (because the change tracker doesn't hold a
    /// reference). See <see cref="IProductRepository.GetByIdReadOnlyAsync"/>
    /// for the full rationale of when to use this vs <see cref="GetByIdAsync"/>.
    /// </remarks>
    public async Task<Product?> GetByIdReadOnlyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // AsNoTracking tells EF Core to materialize the entity WITHOUT adding
        // it to the change tracker. The owned Money Price and the owned
        // PurchaseLimits collection are also materialized without tracking.
        //
        // FirstOrDefaultAsync (not FindAsync) because FindAsync ALWAYS
        // returns a tracked entity — there's no AsNoTracking overload of
        // FindAsync. FirstOrDefaultAsync with a Where predicate is the
        // canonical AsNoTracking read pattern.
        //
        // Price + PurchaseLimits auto-load (owned) — no .Include() needed.
        // ------------------------------------------------------------------
        return await _db.Products
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // AnyAsync translates to SELECT TOP(1) 1 FROM Products WHERE Id = @id —
        // faster than CountAsync == 1 because it short-circuits on the first match.
        return await _db.Products.AnyAsync(p => p.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
    {
        // excludeId is used when renaming — we want to know if ANOTHER product
        // (not the one being renamed) already has this name. Null excludeId
        // means "any product with this name" (used by CreateProduct).
        if (excludeId is null)
        {
            return await _db.Products.AnyAsync(p => p.Name == name, cancellationToken);
        }

        var excludedId = excludeId.Value;
        return await _db.Products.AnyAsync(p => p.Name == name && p.Id != excludedId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddAsync(Product product, CancellationToken cancellationToken = default)
    {
        // AddAsync queues the entity for INSERT on the next SaveChangesAsync.
        // It does NOT hit the DB yet — the IUnitOfWork controls the commit.
        // Price (owned) and PurchaseLimits (owned) are added automatically
        // because they're part of the Product aggregate's table layout.
        await _db.Products.AddAsync(product, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountInStockAsync(CancellationToken cancellationToken = default)
    {
        // SELECT COUNT(*) FROM Products WHERE StockQuantity > 0
        // CountAsync with a predicate translates to a SQL COUNT(*) — no
        // entity materialization, no tracking. Fast even on large catalogs
        // since StockQuantity isn't indexed (we'd need an index if this
        // query ever shows up in perf profiling).
        return await _db.Products
            .CountAsync(p => p.StockQuantity > 0, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllProductIdsAsync(CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Lightweight ID-only query: SELECT Id FROM Products
        // No entity materialization, no tracking. Used by the bulk-default
        // flow in CreateCustomerGroupCommandHandler (Step 5) to iterate all
        // existing products without loading them all into memory at once.
        //
        // The handler batches through these IDs (default 200 per batch),
        // loading tracked products via GetByIdsAsync for each batch,
        // calling SetPurchaseLimit on each, and SaveChanges per batch.
        // ------------------------------------------------------------------
        return await _db.Products
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
    }
}
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
/// OWNED VALUE OBJECTS AUTO-LOADED:
///   <see cref="Product.Price"/> (Money, OwnsOne) and
///   <see cref="Product.PurchaseLimits"/> (CustomerGroupPurchaseLimit, OwnsMany)
///   are part of the Product aggregate's table layout. EF Core loads them
///   automatically whenever a Product is materialized — no <c>.Include()</c>
///   needed. This is the difference between OWNED types (always loaded with
///   their owner) and NAVIGATION properties (separate entity, lazy/explicit
///   load required).
///
/// SEARCH-TERM SEMANTICS:
///   <c>searchTerm</c> matches <see cref="Product.Name"/> with a
///   case-insensitive <c>Contains</c>. SQL Server translates
///   <c>string.Contains</c> with <c>StringComparison.OrdinalIgnoreCase</c>
///   into a <c>LIKE '%term%'</c> expression. We use the EF.Functions.Like
///   free-text helper indirectly via <c>Contains</c> — both produce the same
///   SQL but <c>Contains</c> is more readable.
///   (Note: for case-insensitive matching on a case-insensitive collation
///   database, plain <c>Contains</c> works. For case-sensitive collations,
///   we'd need <c>EF.Functions.Like(p.Name, $"%{term}%")</c> with explicit
///   LOWER() — but the default SQL Server collation is case-insensitive.)
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

    /// <inheritdoc />
    public async Task<PaginatedResult<Product>> GetPaginatedAsync(
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null,
        string? searchTerm = null,
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

        // ------------------------------------------------------------------
        // Build the filter query. We use IQueryable so that all Where clauses
        // are composed into a SINGLE SQL statement (rather than fetching all
        // rows then filtering in memory). Each conditional Where is a no-op
        // when the filter value is null.
        // ------------------------------------------------------------------
        var query = _db.Products.AsQueryable();

        if (categoryId is not null)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }

        if (subCategoryId is not null)
        {
            query = query.Where(p => p.SubCategoryId == subCategoryId.Value);
        }

        if (subSubCategoryId is not null)
        {
            query = query.Where(p => p.SubSubCategoryId == subSubCategoryId.Value);
        }

        // searchTerm matches Product.Name (case-insensitive Contains).
        // We trim and skip empty/null so an empty search box doesn't accidentally
        // filter out products whose names are null (shouldn't happen, but cheap
        // to guard).
        var trimmedSearch = searchTerm?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedSearch))
        {
            query = query.Where(p => p.Name.Contains(trimmedSearch));
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
        // Without it, EF Core throws at runtime. Name is a sensible default
        // (alphabetical browse); could be parameterized later.
        // ------------------------------------------------------------------
        var items = await query
            .OrderBy(p => p.Name)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<Product>(items, totalCount, pageNumber, pageSize);
    }

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
}
using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Categories.Entities;

namespace TakOne.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ICategoryRepository"/>.
///
/// HIERARCHY LOADING:
///   The Category aggregate spans THREE tables: Categories, SubCategories,
///   SubSubCategories. SubCategories is a NAVIGATION collection (one-to-many,
///   separate entity), NOT an owned type — so EF Core does NOT auto-load it
///   when you load a Category. You need explicit <c>.Include()</c>.
///
///   <see cref="GetByIdWithHierarchyAsync"/> loads the full tree in ONE SQL
///   round-trip via <c>.Include(c => c.SubCategories).ThenInclude(s => s.SubSubCategories)</c>.
///   EF Core 7+ can split this into multiple SQL queries for performance
///   (<c>AsSplitQuery()</c>) — but for category trees that rarely exceed a few
///   dozen rows, a single cartesian-product query is fine and faster overall
///   (one round-trip vs three).
///
/// TRACKING POLICY:
///   Same as <see cref="ProductRepository"/>: command handlers load a Category,
///   call domain methods on it (e.g. <c>AddSubCategory</c>, <c>Deactivate</c>),
///   then call <c>IUnitOfWork.SaveChangesAsync</c>. So reads are TRACKED.
///
/// CROSS-LEVEL HIERARCHY CHECKS:
///   <see cref="SubCategoryBelongsToCategoryAsync"/> and
///   <see cref="SubSubCategoryBelongsToSubCategoryAsync"/> query the child
///   tables directly (rather than loading the whole Category aggregate and
///   walking the tree in memory). This is much cheaper for the
///   CreateProduct / UpdateProductCategory validators, which only need a
///   yes/no answer.
/// </summary>
public sealed class CategoryRepository : ICategoryRepository
{
    private readonly ApplicationDbContext _db;

    public CategoryRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // FindAsync returns a tracked Category WITHOUT SubCategories (they're
        // a navigation, not owned). Suitable for handlers that need to call
        // Category-level methods only (Rename, Activate, Deactivate).
        // For handlers that need SubCategories (AddSubCategory, AddSubSubCategory,
        // etc.), use GetByIdWithHierarchyAsync.
        return await _db.Categories.FindAsync(new object[] { id }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Category?> GetByIdWithHierarchyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Eager-load the full tree: Category → SubCategories → SubSubCategories.
        // ThenInclude drills one more level down.
        //
        // We use FirstOrDefaultAsync (not FindAsync) because FindAsync does
        // not support Include. The trade-off: FindAsync checks the change
        // tracker first (faster if already loaded); FirstOrDefaultAsync always
        // hits the DB. For hierarchy queries this is acceptable because they're
        // less cacheable (the tree is large enough that re-querying is cheaper
        // than holding it in the tracker).
        return await _db.Categories
            .Include(c => c.SubCategories)
                .ThenInclude(s => s.SubSubCategories)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<Category>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        // Shop view: list only active Categories for browsing. We deliberately
        // DO NOT include SubCategories here — the shop UI fetches them lazily
        // via GetByIdWithHierarchyAsync when a user expands a Category. This
        // keeps the initial list query cheap.
        //
        // Order by Name for stable UI rendering.
        return await _db.Categories
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.Categories.AnyAsync(c => c.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default)
    {
        // excludeId: see ProductRepository.NameExistsAsync for the rationale.
        if (excludeId is null)
        {
            return await _db.Categories.AnyAsync(c => c.Name == name, cancellationToken);
        }

        var excludedId = excludeId.Value;
        return await _db.Categories.AnyAsync(c => c.Name == name && c.Id != excludedId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> SubCategoryBelongsToCategoryAsync
        (
        Guid categoryId,
        Guid subCategoryId,
        CancellationToken cancellationToken = default
        )
    {
        // Query the SubCategories table directly. The unique composite index
        // on (CategoryId, Name) and the PK on Id make this query fast.
        //
        // We don't load the Category aggregate because that would require
        // .Include(SubCategories) just to check one row — wasteful.
        return await _db.SubCategories
            .AnyAsync(s => s.Id == subCategoryId && s.CategoryId == categoryId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> SubSubCategoryBelongsToSubCategoryAsync
        (
        Guid subCategoryId,
        Guid subSubCategoryId,
        CancellationToken cancellationToken = default
        )
    {
        return await _db.SubSubCategories
            .AnyAsync(ss => ss.Id == subSubCategoryId && ss.SubCategoryId == subCategoryId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        // SubCategories / SubSubCategories added via Category.AddSubCategory /
        // SubCategory.AddSubSubCategory are automatically tracked by EF Core
        // because they're reachable from the tracked root (Category). No
        // separate AddAsync call needed for them.
        await _db.Categories.AddAsync(category, cancellationToken);
    }
}
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
    public async Task<Category?> GetByIdWithHierarchyNoTrackingAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Same shape as GetByIdWithHierarchyAsync, but AsNoTracking — the
        // returned Category (and its SubCategories / SubSubCategories) is
        // NOT attached to the change tracker.
        //
        // Use this from command handlers that need to read the aggregate's
        // state for validation (e.g. checking name uniqueness against existing
        // children) but only want to persist a SINGLE new child entity. By
        // keeping the parent untracked, SaveChanges generates exactly one
        // INSERT and zero UPDATEs — sidestepping the Blazor Server scoped-
        // DbContext stale-tracking bug that causes DbUpdateConcurrencyException.
        //
        // See ICategoryRepository.GetByIdWithHierarchyNoTrackingAsync xmldoc
        // for the full rationale.
        return await _db.Categories
            .AsNoTracking()
            .Include(c => c.SubCategories)
                .ThenInclude(s => s.SubSubCategories)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<Category>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        // Returns all active Categories WITH their full SubCategory →
        // SubSubCategory hierarchy eagerly loaded in a single round-trip.
        //
        // WHY WE INCLUDE THE HIERARCHY HERE (not lazy):
        //   The Application layer's GetActiveCategoriesQueryHandler projects
        //   c.SubCategories and sc.SubSubCategories straight into the DTOs.
        //   If we don't Include them here, EF Core returns empty navigation
        //   collections (SubCategory is a separate entity, NOT an owned
        //   type — EF Core never auto-loads it), and the admin Categories
        //   page silently shows "0 sub-categories" under every category.
        //   Refreshing doesn't help because the data was never queried.
        //
        //   The previous "load lazily when expanded" comment was wishful
        //   thinking — no caller actually does that. Both the shop sidebar
        //   (Products.razor) and the admin tree (AdminCategories.razor)
        //   iterate the returned DTOs immediately, so they need the full
        //   tree up-front.
        //
        // PERFORMANCE:
        //   The Categories table is small (typically a few dozen rows total
        //   across all three levels), so the extra JOINs are negligible.
        //   AsSplitQuery is intentionally NOT used — one round-trip beats
        //   three for a tree this small.
        //
        // Order by Name for stable UI rendering.
        return await _db.Categories
            .Include(c => c.SubCategories)
                .ThenInclude(s => s.SubSubCategories)
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<Category>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        // Returns ALL Categories (active AND inactive) with the full
        // SubCategory → SubSubCategory hierarchy eagerly loaded.
        //
        // WHY NO IsActive FILTER (unlike GetAllActiveAsync):
        //   This is the ADMIN view. When an admin deactivates a Category,
        //   the UI must STILL render that Category (with a red outline +
        //   an "activate" toggle button) so the admin can see it and
        //   reactivate it. If we filtered here, the deactivated category
        //   would simply vanish from the page — and so would all its
        //   children (because the .Include only fires for categories that
        //   pass the .Where filter). That was the v5 bug: deactivating a
        //   Category made it AND its entire subtree disappear, even though
        //   the markup already had the red-outline styling ready.
        //
        //   The deactivated Category's children may be active OR inactive
        //   (DeactivateCategoryCommand cascades, but the admin can also
        //   deactivate a single SubCategory in isolation). Either way we
        //   load them — the UI decides how to render based on each
        //   entity's own IsActive flag.
        //
        // ORDER:
        //   Active first, then inactive, each group sorted by Name. This
        //   keeps the "live" tree at the top of the admin view (where the
        //   admin's attention usually is) while still surfacing every
        //   deactivated node below for easy review / reactivation.
        //   The Application-layer handler does NOT re-sort — it relies on
        //   this ordering, so any change here propagates directly to the
        //   rendered tree.
        return await _db.Categories
            .Include(c => c.SubCategories)
                .ThenInclude(s => s.SubSubCategories)
            .OrderBy(c => c.IsActive ? 0 : 1)
                .ThenBy(c => c.Name)
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
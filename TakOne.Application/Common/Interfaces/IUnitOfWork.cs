namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Coordinates persistence across multiple repositories.
/// Ensures all changes in a single use case are saved atomically.
///
/// Why no Update method?
///   EF Core tracks changes to entities loaded from the database. When a handler
///   loads an aggregate, modifies it, and calls SaveChangesAsync(), EF Core
///   automatically detects the changes and generates UPDATE statements.
///   An explicit Update method is redundant.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detaches ALL tracked entities from the change tracker.
    /// Call this at the START of a handler that loads an existing aggregate
    /// and modifies it (e.g. AddSubCategory, AddSubSubCategory) when running
    /// under Blazor Server.
    ///
    /// WHY THIS EXISTS:
    ///   In Blazor Server, the DbContext is scoped to the user's CIRCUIT
    ///   (the entire SignalR connection), not to a single HTTP request. This
    ///   means the change tracker accumulates entities across multiple
    ///   operations: a Category loaded by GetActiveCategoriesQuery on page
    ///   load is STILL tracked when the user later clicks "Add sub-category".
    ///
    ///   When the handler then calls GetByIdWithHierarchyAsync, EF Core's
    ///   identity resolution returns the ALREADY-TRACKED instance. The
    ///   .Include(SubCategories) query merges into it. On SaveChanges,
    ///   DetectChanges() (called by EF Core + Wolverine's DomainEventScraper
    ///   interceptor) sees the _subCategories collection change and may mark
    ///   the parent Category as Modified — generating a spurious UPDATE that
    ///   fails with DbUpdateConcurrencyException ("expected 1, affected 0").
    ///
    ///   Clearing the tracker at handler entry ensures the aggregate is
    ///   loaded FRESH, with no stale tracking state. Only the new child
    ///   entity ends up in the Added state; the parent stays Unchanged.
    ///
    /// WHY NOT ALWAYS CALL IT:
    ///   Handlers that create a NEW aggregate (CreateCategoryCommandHandler,
    ///   CreateStaffCommandHandler, etc.) don't need this — they call
    ///   repository.AddAsync(), which explicitly tracks the new entity.
    ///   Clearing the tracker there would be harmless but pointless.
    ///
    /// NOTE:
    ///   In practice, <c>ClearChangeTracker</c> alone is NOT enough to
    ///   prevent the stale-tracking bug — the subsequent tracked
    ///   <see cref="ICategoryRepository.GetByIdWithHierarchyAsync"/> call
    ///   re-attaches the parent + children, and DetectChanges can still
    ///   mark them Modified. The reliable pattern is to load the aggregate
    ///   <c>AsNoTracking</c> (see
    ///   <see cref="ICategoryRepository.GetByIdWithHierarchyNoTrackingAsync"/>)
    ///   and then explicitly track ONLY the new child via
    ///   <see cref="AddEntity"/>. <c>ClearChangeTracker</c> is retained
    ///   for backward compatibility and for handlers that genuinely need
    ///   a fresh tracked load.
    /// </summary>
    void ClearChangeTracker();

    /// <summary>
    /// Explicitly marks the given entity as <c>Added</c> in EF Core's change
    /// tracker. Use this when you've loaded an aggregate <c>AsNoTracking</c>
    /// (to avoid the Blazor Server scoped-DbContext stale-tracking bug) and
    /// want to persist a SINGLE new child entity.
    ///
    /// WHY THIS IS NEEDED:
    ///   When a handler loads a parent Category via
    ///   <c>GetByIdWithHierarchyNoTrackingAsync</c>, the parent and its
    ///   existing children are NOT tracked. Mutating the parent's
    ///   <c>_subCategories</c> collection (via <c>AddSubCategory</c>) has
    ///   no effect on the tracker — the new SubCategory is just an in-memory
    ///   object. To persist it, the handler must explicitly tell EF Core
    ///   "track this as a new entity" — that's what <c>AddEntity</c> does.
    ///
    ///   After <c>AddEntity(newSubCategory)</c>, the change tracker contains
    ///   exactly ONE entity (the new SubCategory) in the <c>Added</c> state.
    ///   <c>SaveChangesAsync</c> then generates exactly one INSERT and zero
    ///   UPDATEs — the parent and siblings are untracked, so they cannot
    ///   be marked <c>Modified</c> and no spurious UPDATE can be generated.
    ///
    /// WHY THIS IS PREFERRED OVER <c>ClearChangeTracker</c>:
    ///   <c>ClearChangeTracker</c> clears the tracker, but a subsequent
    ///   tracked query re-populates it. <c>AddEntity</c> works with an
    ///   <c>AsNoTracking</c> read, so the tracker stays empty except for
    ///   the single new entity the handler wants to persist.
    /// </summary>
    void AddEntity(object entity);
}
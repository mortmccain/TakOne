using TakOne.Domain.Categories.Entities;

namespace TakOne.Application.Common.Interfaces;

public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a category with its full hierarchy (SubCategories and
    /// SubSubCategories) eagerly loaded. Used by the shop view to render
    /// the category tree.
    /// </summary>
    Task<Category?> GetByIdWithHierarchyAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="GetByIdWithHierarchyAsync"/> but returns the
    /// aggregate <c>AsNoTracking</c> — the entities are NOT attached to
    /// EF Core's change tracker.
    ///
    /// WHY THIS EXISTS:
    ///   The Blazor Server scoped-DbContext stale-tracking bug: when a
    ///   handler loads a parent Category (tracked), mutates its
    ///   <c>_subCategories</c> collection via <c>AddSubCategory</c>, then
    ///   calls <c>SaveChangesAsync</c>, EF Core's <c>DetectChanges</c>
    ///   (which runs automatically before save) sees the collection change
    ///   and may mark the parent Category (or its existing children) as
    ///   <c>Modified</c>. That generates a spurious UPDATE whose WHERE
    ///   clause matches 0 rows, throwing
    ///   <c>DbUpdateConcurrencyException: expected 1, affected 0</c>.
    ///
    ///   <see cref="IUnitOfWork.ClearChangeTracker"/> was the first attempt
    ///   at a fix, but it doesn't reliably prevent the issue — the tracked
    ///   <c>Include</c> query re-attaches the parent + children, and the
    ///   bug reappears on <c>DetectChanges</c>.
    ///
    ///   The reliable fix is to load the aggregate <c>AsNoTracking</c> so
    ///   it's never in the tracker at all. The handler then explicitly
    ///   tracks ONLY the new child entity via
    ///   <see cref="IUnitOfWork.AddEntity"/>. <c>SaveChanges</c> then
    ///   generates exactly one INSERT (for the new child) and zero
    ///   UPDATEs — the parent and siblings are not tracked, so they
    ///   cannot be marked <c>Modified</c>.
    /// </summary>
    Task<Category?> GetByIdWithHierarchyNoTrackingAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all active Categories with their full SubCategory →
    /// SubSubCategory hierarchy eagerly loaded. Used by both the shop
    /// sidebar (Products page) and the admin Categories tree page.
    /// </summary>
    Task<List<Category>> GetAllActiveAsync(CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> NameExistsAsync(string name, Guid? excludeId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether the given SubCategoryId belongs to the given CategoryId.
    /// Used by validators when creating/updating Products to enforce the
    /// cross-aggregate hierarchy invariant.
    /// </summary>
    Task<bool> SubCategoryBelongsToCategoryAsync(
        Guid categoryId,
        Guid subCategoryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether the given SubSubCategoryId belongs to the given SubCategoryId.
    /// </summary>
    Task<bool> SubSubCategoryBelongsToSubCategoryAsync(
        Guid subCategoryId,
        Guid subSubCategoryId,
        CancellationToken cancellationToken = default);

    Task AddAsync(Category category, CancellationToken cancellationToken = default);


}
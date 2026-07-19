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

namespace TakOne.Application.Categories.DTOs;

/// <summary>
/// Read-side DTO for a full Category, including its full hierarchy of
/// SubCategories and SubSubCategories.
///
/// Used by the admin category-management page and by the shop's category
/// tree view. The hierarchy is materialized as nested lists because the
/// shop renders the tree client-side and does not want to re-issue queries
/// for each level.
/// </summary>
public sealed class CategoryDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }

    /// <summary>
    /// Child SubCategories. Empty (not null) when the Category has no children.
    /// </summary>
    public List<SubCategoryDto> SubCategories { get; init; } = new();
}
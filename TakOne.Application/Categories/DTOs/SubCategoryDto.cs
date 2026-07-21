namespace TakOne.Application.Categories.DTOs;

/// <summary>
/// Read-side DTO for a SubCategory, including its child SubSubCategories.
/// Always nested inside <see cref="CategoryDto"/> — never returned standalone
/// from a query, because every shop/admin view that shows a SubCategory also
/// needs to know its parent Category for context.
/// </summary>
public sealed class SubCategoryDto
{
    public Guid Id { get; init; }
    public Guid CategoryId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }

    public List<SubSubCategoryDto> SubSubCategories { get; init; } = new();
}
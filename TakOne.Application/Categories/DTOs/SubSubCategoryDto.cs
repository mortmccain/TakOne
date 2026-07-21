namespace TakOne.Application.Categories.DTOs;

/// <summary>
/// Read-side DTO for a SubSubCategory. Always nested inside
/// <see cref="SubCategoryDto"/>.
/// </summary>
public sealed class SubSubCategoryDto
{
    public Guid Id { get; init; }
    public Guid SubCategoryId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}
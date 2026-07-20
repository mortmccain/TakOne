using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Products.DTOs;

/// <summary>
/// Lightweight DTO for displaying products in a paginated list (shop view,
/// admin catalog view, etc.). Purchase limits are NOT included — fetch the
/// full <see cref="ProductDto"/> if you need them.
/// </summary>
public sealed class ProductListItemDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }

    public MoneyDto Price { get; init; } = new();

    public int StockQuantity { get; init; }

    public Guid CategoryId { get; init; }
    public Guid? SubCategoryId { get; init; }
    public Guid? SubSubCategoryId { get; init; }
}
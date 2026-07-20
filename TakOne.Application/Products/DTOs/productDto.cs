using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Products.DTOs;

/// <summary>
/// Read-side DTO for a full Product, including its per-group purchase limits.
///
/// Money is exposed as <see cref="MoneyDto"/> (Amount + Currency) so the API
/// layer doesn't need to know about the domain <c>Money</c> value object.
/// </summary>
public sealed class ProductDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }

    public MoneyDto Price { get; init; } = new();

    public int StockQuantity { get; init; }

    public Guid CategoryId { get; init; }
    public Guid? SubCategoryId { get; init; }
    public Guid? SubSubCategoryId { get; init; }

    public List<ProductPurchaseLimitDto> PurchaseLimits { get; init; } = new();
}
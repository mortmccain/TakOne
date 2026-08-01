using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Products.DTOs;

/// <summary>
/// Lightweight DTO for displaying products in a paginated list (shop view,
/// admin catalog view, etc.).
/// </summary>
public sealed class ProductListItemDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Short product description shown on the shop card under the product name.
    /// Populated from <c>Product.Description</c>. May be empty string if the
    /// product was created without a description.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    public string? PictureUrl { get; init; }

    public MoneyDto Price { get; init; } = new();

    public int StockQuantity { get; init; }

    public Guid CategoryId { get; init; }
    public Guid? SubCategoryId { get; init; }
    public Guid? SubSubCategoryId { get; init; }

    /// <summary>
    /// The per-product purchase limit that applies to the CURRENT caller's
    /// customer group, or <c>null</c> if:
    ///   - the caller is staff (no GroupName → no per-product cap), or
    ///   - the caller's group has no specific limit set on this product
    ///     (in which case the Sale aggregate enforces no cap either).
    ///
    /// The shop card uses this to clamp the quantity selector and gray out
    /// the add-to-cart button when the user has already selected the max.
    /// </summary>
    public int? MyPurchaseLimit { get; init; }
}
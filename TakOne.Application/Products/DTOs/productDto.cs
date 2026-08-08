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

    /// <summary>
    /// The per-product purchase limit that applies to the CURRENT caller's
    /// customer group, or <c>null</c> if:
    ///   - the caller is staff (no GroupName → no per-product cap), or
    ///   - the caller's group has no specific limit set on this product.
    ///
    /// Populated by <see cref="GetProductByIdQueryHandler"/> via
    /// <c>Product.GetPurchaseLimitForGroup(currentUser.GroupName)</c>.
    ///
    /// The ProductDetail page uses this to:
    ///   - clamp the quantity selector's Max to the customer's limit
    ///     (so they cannot even SELECT more than their group allows), and
    ///   - show a "My purchase limit: N" hint below the selector.
    ///
    /// Staff callers (Admin / Manager / Employee) get <c>null</c> — they
    /// have no per-product cap, the selector's Max falls back to a large
    /// number bounded only by stock (which staff don't see anyway).
    /// </summary>
    public int? MyPurchaseLimit { get; init; }
}
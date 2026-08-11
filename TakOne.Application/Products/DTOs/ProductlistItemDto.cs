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

    // ── CATEGORY HIERARCHY DISPLAY FIELDS ──────────────────────────────
    //
    // These are populated by GetProductsPaginatedQueryHandler, which loads
    // the category tree once (via ICategoryRepository.GetAllAsync) and
    // resolves each product's CategoryId / SubCategoryId / SubSubCategoryId
    // against it. We do the resolution in the handler (not in the database
    // via joins) because the category hierarchy lives in a different
    // aggregate and the repository contract returns aggregates, not
    // projections.
    //
    // The IsActive flags are surfaced so the UI can render "(deactivated)"
    // next to a category name when the referenced category has been soft-
    // deleted. A product can reference a deactivated category because the
    // foreign key is just a Guid — the category's IsActive flag doesn't
    // affect referential integrity. The admin needs to SEE that the
    // category is deactivated (and decide whether to re-categorize the
    // product), not have it silently hidden.

    /// <summary>
    /// Display name of the product's top-level Category. Empty string if the
    /// Category could not be resolved (e.g. was hard-deleted — should not
    /// happen in normal operation since Category uses soft-delete).
    /// </summary>
    public string CategoryName { get; init; } = string.Empty;

    /// <summary>
    /// True if the product's Category has been deactivated (soft-deleted).
    /// The UI appends "(deactivated)" to the category name when this is true.
    /// </summary>
    public bool CategoryIsActive { get; init; } = true;

    /// <summary>
    /// Display name of the product's SubCategory, or empty string if the
    /// product has no SubCategory assigned.
    /// </summary>
    public string SubCategoryName { get; init; } = string.Empty;

    /// <summary>
    /// True if the product's SubCategory has been deactivated. The UI
    /// appends "(deactivated)" when true.
    /// </summary>
    public bool SubCategoryIsActive { get; init; } = true;

    /// <summary>
    /// Display name of the product's SubSubCategory, or empty string if the
    /// product has no SubSubCategory assigned.
    /// </summary>
    public string SubSubCategoryName { get; init; } = string.Empty;

    /// <summary>
    /// True if the product's SubSubCategory has been deactivated.
    /// </summary>
    public bool SubSubCategoryIsActive { get; init; } = true;

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

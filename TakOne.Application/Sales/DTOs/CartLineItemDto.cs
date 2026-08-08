using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// A single line on the user's active shopping cart. See <see cref="CartDto"/>
/// for the rationale behind having a cart-specific line DTO (vs. reusing
/// <see cref="SaleLineItemDto"/>).
///
/// FIELD MAPPING vs. <see cref="SaleLineItemDto"/>:
///   Id, LineNumber, ProductId, ProductName, Quantity, UnitPrice, LineTotal:
///     identical to <see cref="SaleLineItemDto"/> — same source columns
///     on the SaleLineItem table.
///   CurrentStock:
///     NEW field, populated by the cart query handler from the live
///     Product.StockQuantity at query time. Used by the /Cart UI to:
///       - show "Only N in stock" warning when CurrentStock &lt; Quantity
///       - show "Out of stock" badge when CurrentStock == 0
///       - clamp the quantity selector's Max to Math.Max(CurrentStock, Quantity)
///         (we allow setting the existing quantity even if it exceeds stock,
///         so the user can deliberately reduce it; but we don't allow
///         INCREASING past stock)
///       - disable the Submit button if any line's Quantity &gt; CurrentStock
/// </summary>
public sealed class CartLineItemDto
{
    public Guid Id { get; init; }

    /// <summary>
    /// Audit-friendly position of this line on the sale (1, 2, 3, ...).
    /// Stable: deleting line 2 does NOT renumber line 3. Inherited
    /// behavior from <see cref="TakOne.Domain.Sales.Entities.SaleLineItem"/>.
    /// </summary>
    public int LineNumber { get; init; }

    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }

    /// <summary>
    /// Per-unit price snapshot taken when the line was added. Stored on the
    /// line as a snapshot (not a reference to Product.Price) so that future
    /// price changes on the Product don't alter historical sales — but for
    /// a DRAFT (cart), the price is still the snapshot at add-time, not the
    /// current Product price. This is intentional: if a price changes while
    /// the user is shopping, their cart keeps the old price until they
    /// re-add the item. Submitting "locks in" the snapshot price.
    /// </summary>
    public MoneyDto UnitPrice { get; init; } = new();

    /// <summary>
    /// Quantity × UnitPrice. Today identical to GrossTotal (no discount/tax
    /// modeling). See <see cref="SaleLineItemDto.LineTotal"/> for the full
    /// "GrossTotal vs. LineTotal" rationale.
    /// </summary>
    public MoneyDto LineTotal { get; init; } = new();

    /// <summary>
    /// Live stock for this line's Product at the moment the cart was loaded.
    /// See the class-level XML doc for how the /Cart UI uses this field.
    /// </summary>
    public int CurrentStock { get; init; }

    /// <summary>
    /// The per-product purchase limit that applies to the CURRENT caller's
    /// customer group for this line's Product, or <c>null</c> if:
    ///   - the caller is staff (no GroupName → no per-product cap), or
    ///   - the caller's group has no specific limit set on this product.
    ///
    /// Populated by <see cref="GetActiveCartForUserQueryHandler"/> from the
    /// live Product aggregate (same load that fills <see cref="CurrentStock"/>).
    ///
    /// The cart UI uses this to clamp the per-line quantity selector's Max
    /// to <c>Min(MyPurchaseLimit, Max(CurrentStock, Quantity))</c> so the user
    /// CANNOT select a quantity above their group's limit (the backend
    /// <see cref="UpdateSaleLineItemCommandHandler"/> would reject it with a
    /// DomainException, but blocking the selection in the UI is the
    /// correct UX — no "I clicked +6 and got a generic error" surprise).
    /// </summary>
    public int? MyPurchaseLimit { get; init; }
}
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Read-side DTO for a single line on a Sale.
///
/// MONEY FIELDS — TERMINOLOGY:
///   - <see cref="UnitPrice"/>:  The per-unit price snapshot at the time the
///     line was added. Stored on the domain as a snapshot (not a reference
///     to Product.Price) so future price changes don't alter historical sales.
///
///   - <see cref="GrossTotal"/>: Quantity × UnitPrice. Computed by the domain
///     <c>SaleLineItem.GrossTotal</c> property. Represents the unadjusted
///     amount for the line — no discounts, taxes, or surcharges applied.
///
///   - <see cref="LineTotal"/>:  The amount the customer is actually charged
///     for this line. TODAY this is identical to <see cref="GrossTotal"/>
///     because no discount/tax logic exists in the domain yet. The field is
///     included in the DTO contract now so that when discount/tax modeling
///     is added to the domain later, only the handler projection changes —
///     the wire format stays stable and clients don't break.
///
///     WHEN to read which field:
///       - UI "sticker" total (pre-discount display):  GrossTotal
///       - UI "you will be charged" total:             LineTotal
///       - Receipts, invoices, financial reports:      LineTotal
///       - Audit "what was the raw computation":       GrossTotal + UnitPrice
/// </summary>
public sealed class SaleLineItemDto
{
    public Guid Id { get; init; }

    /// <summary>
    /// Audit-friendly position of this line on the sale (1, 2, 3, ...).
    /// Stable: deleting line 2 does NOT renumber line 3.
    /// </summary>
    public int LineNumber { get; init; }

    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }

    public MoneyDto UnitPrice { get; init; } = new();
    public MoneyDto GrossTotal { get; init; } = new();
    public MoneyDto LineTotal { get; init; } = new();
}
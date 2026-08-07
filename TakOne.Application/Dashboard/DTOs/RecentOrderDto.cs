namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One row in the "Recent Orders" table on the redesigned Dashboard.
/// Shows the 6 most-recent submitted sales (any non-Draft status).
/// </summary>
public sealed class RecentOrderDto
{
    /// <summary>
    /// Human-readable sale number (e.g. "TM-2389"). Snapshot from
    /// <c>Sale.SaleNumber</c>.
    /// </summary>
    public string SaleNumber { get; set; } = string.Empty;

    /// <summary>
    /// Customer's display name (snapshot from <c>Sale.CustomerName</c>).
    /// </summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>
    /// First product name on the sale + quantity, for the "Product" column.
    /// Pre-formatted server-side (e.g. "ماکارونی پروانه × ۳") so the razor
    /// page can render it directly.
    /// </summary>
    public string ProductSummary { get; set; } = string.Empty;

    /// <summary>
    /// Sale total amount, already converted to display currency.
    /// </summary>
    public decimal TotalAmount { get; set; }

    /// <summary>
    /// Status as a string (e.g. "Pending", "Approved"). Pre-stringified so
    /// the razor page can map to a CSS badge class without referencing the
    /// <c>SaleStatus</c> enum.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Submitted timestamp (UTC). The razor page formats this as a relative
    /// "X minutes ago" string client-side via JS.
    /// </summary>
    public DateTime? SubmittedAtUtc { get; set; }
}
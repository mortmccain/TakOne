namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One row in the "Top Products" horizontal bar chart on the redesigned
/// Dashboard. The chart shows the top N products by TOTAL SALES AMOUNT
/// (sum of <c>SaleLineItem.GrossTotal.Amount</c>) across revenue-eligible
/// sales (Pending + Approved + Invoiced) submitted in the last 30 days.
/// </summary>
public sealed class TopProductDto
{
    /// <summary>
    /// Product name (snapshot from <c>SaleLineItem.ProductName</c> — no
    /// join to the Product table needed).
    /// </summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>
    /// Total quantity sold across all revenue-eligible sales in the last
    /// 30 days (sum of <c>SaleLineItem.Quantity</c>). Kept for tooltip
    /// enrichment and potential future use; the bar chart's X-axis is
    /// driven by <see cref="TotalAmount"/>.
    /// </summary>
    public int QuantitySold { get; set; }

    /// <summary>
    /// Total sales amount (revenue) for this product across all
    /// revenue-eligible sales (Pending + Approved + Invoiced) submitted
    /// in the last 30 days, summed across ALL customers. Already converted
    /// to display currency (Toman when original is IRR — divided by 10)
    /// by the handler. This is what the bar chart's X-axis plots.
    /// </summary>
    public decimal TotalAmount { get; set; }
}

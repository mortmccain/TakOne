namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One row in the "Top Products" horizontal bar chart on the redesigned
/// Dashboard. The chart shows the top N products by total quantity sold
/// across approved (or invoiced) sales in the last 30 days.
/// </summary>
public sealed class TopProductDto
{
    /// <summary>
    /// Product name (snapshot from <c>SaleLineItem.ProductName</c> — no
    /// join to the Product table needed).
    /// </summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>
    /// Total quantity sold across all approved+invoiced sales in the last
    /// 30 days (sum of <c>SaleLineItem.Quantity</c>).
    /// </summary>
    public int QuantitySold { get; set; }
}
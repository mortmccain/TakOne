namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One row of the Dashboard's restock alert — an ACTIVE product whose
/// stock is at or below <see cref="TakOne.Application.Common.Models.ProductStockThresholds.LowStock"/>
/// (including out-of-stock, which is the most urgent case and therefore
/// sorts first). See <see cref="DashboardStatsDto.LowStockProducts"/>.
/// </summary>
public sealed class LowStockProductDto
{
    /// <summary>Product id — lets the UI deep-link to the product.</summary>
    public Guid Id { get; set; }

    /// <summary>Display name at query time (denormalized snapshot).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Current stock quantity. 0 = out of stock; 1..LowStock = running
    /// low. The alert lists items stock-ascending so the most critical
    /// appear first.
    /// </summary>
    public int StockQuantity { get; set; }
}

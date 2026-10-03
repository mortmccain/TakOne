namespace TakOne.Application.Common.Models;

/// <summary>
/// Shared stock thresholds — ONE definition consumed by every layer that
/// needs the "low stock" concept, so the Dashboard's restock alert and the
/// Admin Products grid's amber stock cell can never drift apart.
///
/// The value 10 preserves the exact semantics the Admin Products grid has
/// shipped since its redesign (stock cell coloring: green = healthy,
/// amber = <c>1..LowStock</c>, red = 0 / deactivated) — this class just
/// gives that magic number a name and a home.
/// </summary>
public static class ProductStockThresholds
{
    /// <summary>
    /// An ACTIVE product whose <c>StockQuantity</c> is at or below this
    /// value is "running low" (and at 0, out). The Dashboard's restock
    /// alert lists these (most critical first: stock ascending); the
    /// Admin Products grid colors their stock cell amber.
    /// </summary>
    public const int LowStock = 10;
}

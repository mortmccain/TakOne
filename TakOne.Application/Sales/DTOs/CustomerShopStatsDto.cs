using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.Queries.GetCustomerShopStats;

/// <summary>
/// DTO powering the 4 numeric stat cards on the /Products shop page header.
///
/// All four values are scoped to the CURRENT caller (the user browsing the
/// shop) — never company-wide. Customers see their own purchase history;
/// staff who self-shop (Admin/Manager/Employee) see their own personal
/// history too (the same scope they get on /Sales).
///
/// The 5th shop stat ("سفارش سریع" / Quick Reorder) is a button, not a
/// value, so it's not represented here.
/// </summary>
public sealed class CustomerShopStatsDto
{
    /// <summary>
    /// Count of the caller's submitted sales (Pending/Approved/Invoiced —
    /// NOT Draft, NOT Cancelled) with SubmittedAtUtc in the current
    /// calendar month (UTC, matches the rest of the app).
    /// </summary>
    public int OrdersThisMonth { get; init; }

    /// <summary>
    /// Sum of <c>Sale.Total.Amount</c> for the caller's submitted sales
    /// in the current calendar month. Currency mirrors the underlying
    /// sales' currency (IRR in production).
    /// </summary>
    public MoneyDto MonthlyTotal { get; init; } = new();

    /// <summary>
    /// Sum of <c>Sale.Total.Amount</c> for the caller's submitted sales
    /// in the current calendar year.
    /// </summary>
    public MoneyDto YearlyTotal { get; init; } = new();

    /// <summary>
    /// Count of distinct products with <c>StockQuantity &gt; 0</c>.
    /// Company-wide value (not user-scoped) — same for every caller.
    /// Cheap to compute (single <c>COUNT(*) WHERE StockQuantity &gt; 0</c>).
    /// </summary>
    public int InStockProductCount { get; init; }
}
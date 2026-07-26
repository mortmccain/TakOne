namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One row in the "5-Year Revenue Distribution" pie chart and the
/// "Revenue by Year" column chart on the Dashboard.
///
/// The two charts consume the SAME data (yearly totals) but render it
/// differently — pie for share-of-total, column for absolute comparison.
/// Both bind to a <c>RadzenChart</c> via <c>CategoryProperty="YearLabel"</c>
/// and <c>ValueProperty="TotalAmount"</c>, so the property names here are
/// part of the API contract with the razor page.
/// </summary>
public sealed class YearlyRevenueDto
{
    /// <summary>
    /// The 4-digit calendar year (e.g. 2026). Used for grouping in the
    /// handler; not bound directly to any chart property.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Pre-formatted label for chart axes/legend (e.g. "2026"). The chart
    /// binds to this string rather than the int so we don't have to write
    /// a format string in the razor markup — keeps the page declarative.
    /// </summary>
    public string YearLabel { get; set; } = string.Empty;

    /// <summary>
    /// Total revenue for this year, summed across all non-cancelled sales
    /// in the caller's scope (all sales for Admin/Manager/ReadOnly; only
    /// sales the Employee approved, per roadmap Section 12.2).
    ///
    /// Cancelled sales are excluded by convention — counting cancelled
    /// sales in "revenue" would understate true performance whenever
    /// cancellations spiked. Drafts are also excluded (not yet revenue).
    /// </summary>
    public decimal TotalAmount { get; set; }
}
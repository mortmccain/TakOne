namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One slice of the "Sales by Status" donut chart on the Dashboard.
///
/// The donut shows the current breakdown of all sales (in the caller's
/// scope) across the five <c>SaleStatus</c> values: Draft, Pending,
/// Approved, Invoiced, Cancelled. Each slice's <c>Count</c> is the raw
/// number of sales in that status; the donut's relative slice sizes give
/// a quick visual sense of the funnel.
/// </summary>
public sealed class StatusCountDto
{
    /// <summary>
    /// The <c>SaleStatus</c> value as a string (e.g. "Approved").
    /// Pre-stringified in the handler so the chart doesn't need to know
    /// about the <c>SaleStatus</c> enum — the razor page binds to this
    /// via <c>CategoryProperty="Status"</c>.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Number of sales in this status, within the caller's scope.
    /// Bound to the chart via <c>ValueProperty="Count"</c>.
    /// </summary>
    public int Count { get; set; }
}
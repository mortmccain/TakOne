namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One day's revenue total, used for the weekly revenue trend line chart
/// on the redesigned Dashboard. The chart plots two series — "this week"
/// and "last week" — each consisting of 7 <see cref="WeeklyRevenueDto"/>
/// points (one per day).
/// </summary>
public sealed class WeeklyRevenueDto
{
    /// <summary>
    /// The day's date (UTC). Used only for grouping/ordering in the handler;
    /// not bound to the chart directly.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Short day-of-week label (e.g. "Sat", "Sun"). The chart's x-axis binds
    /// to this string. Localized to the request culture server-side so the
    /// chart shows Persian weekday abbreviations (ش، ی، د، ...) when the
    /// user is in fa-IR.
    /// </summary>
    public string DayLabel { get; set; } = string.Empty;

    /// <summary>
    /// Total revenue for this day, summed across non-cancelled, non-draft
    /// sales in scope. Already converted to the display currency (Toman when
    /// the original currency is IRR — see handler comment on
    /// <see cref="DashboardStatsDto.DisplayCurrency"/>).
    /// </summary>
    public decimal TotalAmount { get; set; }
}
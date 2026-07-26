namespace TakOne.Application.Dashboard.DTOs;

/// <summary>
/// One row in the "Monthly Revenue — &lt;current year&gt;" line chart on
/// the Dashboard. The chart shows the current calendar year only (12
/// points, Jan → Dec), so this DTO is materialized for all 12 months
/// even if some months have zero revenue — empty months render as a flat
/// segment on the line rather than a gap, which is the correct visual.
/// </summary>
public sealed class MonthlyRevenueDto
{
    /// <summary>
    /// Month number 1–12. Used for ordering in the handler; not bound to
    /// any chart property.
    /// </summary>
    public int Month { get; set; }

    /// <summary>
    /// Pre-formatted short month label (e.g. "Jan", "Feb"). The chart's
    /// <c>RadzenCategoryAxis</c> binds to this string via
    /// <c>CategoryProperty="MonthLabel"</c>.
    ///
    /// LOCALIZATION NOTE: the chart label is currently English-only
    /// (Jan/Feb/Mar...). For full Persian localization we'd need to
    /// either materialize these labels server-side using the request
    /// culture's <c>DateTimeFormatInfo.AbbreviatedMonthNames</c>, or
    /// format them client-side via a JS interop call. Out of scope for
    /// v1 Phase 2 — deferred to Phase 9.2 (Persian localization review).
    /// </summary>
    public string MonthLabel { get; set; } = string.Empty;

    /// <summary>
    /// Total revenue for this month in the current year, summed across
    /// non-cancelled sales in the caller's scope. Zero for future
    /// months (which is fine — the line just stays flat).
    /// </summary>
    public decimal TotalAmount { get; set; }
}
using TakOne.WebUI.Components.Shared;

namespace TakOne.WebUI.Services;

/// <summary>
/// Formats KPI trend-delta chips (Round 4) — the ▲/▼ pill next to a
/// KPI card's value. Shared by the desktop Dashboard and the
/// MobileDashboard so both surfaces render byte-identical semantics.
/// </summary>
/// <remarks>
/// <para>
/// <b>CONVENTIONS</b>:
/// <list type="bullet">
///   <item>Absolute difference for day-over-day COUNTS
///   ("+3 · vs yesterday") — small numbers read better as counts than
///   percentages.</item>
///   <item>Percentage for month-over-month AMOUNTS — the values are
///   large enough for a ratio to be meaningful.</item>
///   <item>previous == 0 &amp;&amp; current &gt; 0 → the caller's
///   "new activity" label (a "+∞%" would be nonsense).</item>
///   <item>current == previous (including both zero) → EMPTY text →
///   the chip hides itself ("nothing to report" is not worth a chip).</item>
///   <item>The minus sign is U+2212 (true minus), not an ASCII hyphen,
///   so the chip typography stays clean in both cultures.</item>
/// </list>
/// </para>
/// <para>
/// <b>WHY THE FORMATTERS ARE INJECTED</b>: digit rendering is
/// culture-dependent (Persian digits in fa-IR). The desktop formats via
/// its page-local helpers, the mobile page via <see cref="CultureFormat"/>
/// directly — passing the formatter in keeps this service culture-free
/// while both pages keep their established formatting conventions.
/// </para>
/// </remarks>
public static class KpiDeltaFormatter
{
    /// <summary>
    /// A count delta as an absolute difference ("+3 · vs yesterday").
    /// </summary>
    /// <param name="current">The current-period count.</param>
    /// <param name="previous">The previous-period count.</param>
    /// <param name="vsSuffix">Localized comparison suffix (e.g. "vs yesterday").</param>
    /// <param name="formatDigits">Culture-aware integer formatter.</param>
    /// <returns>Direction + chip text + tooltip (the raw previous value).</returns>
    public static (KpiDeltaDirection Direction, string Text, string? Title) CountDelta(
        int current,
        int previous,
        string vsSuffix,
        Func<int, string> formatDigits)
    {
        if (current == previous)
        {
            return (KpiDeltaDirection.Flat, string.Empty, null);
        }

        var direction = current > previous ? KpiDeltaDirection.Up : KpiDeltaDirection.Down;
        var diff = Math.Abs(current - previous);
        var sign = current > previous ? "+" : "−"; // true minus (U+2212)
        return (direction, $"{sign}{formatDigits(diff)} · {vsSuffix}", formatDigits(previous));
    }

    /// <summary>
    /// An amount delta as a percentage ("+12% · vs last month"), with
    /// the raw previous amount as the chip's tooltip.
    /// </summary>
    /// <param name="current">The current-period amount.</param>
    /// <param name="previous">The previous-period amount.</param>
    /// <param name="vsSuffix">Localized comparison suffix (e.g. "vs last month").</param>
    /// <param name="previousLabel">The formatted previous amount (tooltip).</param>
    /// <param name="formatPercent">Culture-aware percent formatter.</param>
    /// <param name="newLabel">Localized "new activity" label (shown when
    /// previous is zero and current is not).</param>
    public static (KpiDeltaDirection Direction, string Text, string? Title) AmountDelta(
        decimal current,
        decimal previous,
        string vsSuffix,
        string previousLabel,
        Func<decimal, string> formatPercent,
        string newLabel)
    {
        if (current == previous)
        {
            return (KpiDeltaDirection.Flat, string.Empty, null);
        }

        if (previous == 0)
        {
            // First period with activity — a percentage is undefined.
            return (KpiDeltaDirection.Up, newLabel, previousLabel);
        }

        var direction = current > previous ? KpiDeltaDirection.Up : KpiDeltaDirection.Down;
        var pct = Math.Abs((current - previous) / previous * 100m);
        var sign = current > previous ? "+" : "−";
        return (direction, $"{sign}{formatPercent(pct)} · {vsSuffix}", previousLabel);
    }
}

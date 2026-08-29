using FluentAssertions;
using TakOne.WebUI.Components.Shared;
using TakOne.WebUI.Services;
using Xunit;

namespace TakOne.WebUI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="KpiDeltaFormatter"/> — the KPI trend-delta
/// chip text builder shared by the desktop + mobile dashboards (Round 4).
/// </summary>
/// <remarks>
/// The formatter is pure; the tests pin the CONVENTIONS the chip family
/// relies on: absolute diffs for counts, percentages for amounts, the
/// "new this month" fallback when the previous period is zero, and the
/// HIDE (empty text) case when nothing changed.
/// </remarks>
public class KpiDeltaFormatterTests
{
    private static string Digits(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Percent(decimal p) => $"{p:0.#}%";

    // ── CountDelta (day-over-day counts) ─────────────────────────────

    [Fact]
    public void CountDelta_Up_RendersSignedAbsoluteDiff()
    {
        var (direction, text, title) =
            KpiDeltaFormatter.CountDelta(8, 5, "vs yesterday", Digits);

        direction.Should().Be(KpiDeltaDirection.Up);
        text.Should().Be("+3 · vs yesterday");
        title.Should().Be("5");
    }

    [Fact]
    public void CountDelta_Down_RendersTrueMinus()
    {
        var (direction, text, _) =
            KpiDeltaFormatter.CountDelta(2, 5, "vs yesterday", Digits);

        direction.Should().Be(KpiDeltaDirection.Down);
        // U+2212 minus, not an ASCII hyphen — pinned so the chip
        // typography stays clean in both cultures.
        text.Should().Be("\u22123 · vs yesterday");
    }

    [Fact]
    public void CountDelta_Equal_HidesTheChip()
    {
        var (direction, text, title) =
            KpiDeltaFormatter.CountDelta(5, 5, "vs yesterday", Digits);

        direction.Should().Be(KpiDeltaDirection.Flat);
        text.Should().BeEmpty("nothing to report is not worth a chip");
        title.Should().BeNull();
    }

    [Fact]
    public void CountDelta_BothZero_HidesTheChip()
    {
        var (_, text, _) =
            KpiDeltaFormatter.CountDelta(0, 0, "vs yesterday", Digits);

        text.Should().BeEmpty();
    }

    [Fact]
    public void CountDelta_PreviousZero_RendersNormally()
    {
        // Counts don't have the "undefined percentage" problem — a zero
        // previous count is just zero, so the absolute diff is fine.
        var (direction, text, _) =
            KpiDeltaFormatter.CountDelta(3, 0, "vs yesterday", Digits);

        direction.Should().Be(KpiDeltaDirection.Up);
        text.Should().Be("+3 · vs yesterday");
    }

    // ── AmountDelta (month-over-month amounts) ───────────────────────

    [Fact]
    public void AmountDelta_Up_RendersPercentage()
    {
        var (direction, text, title) =
            KpiDeltaFormatter.AmountDelta(112m, 100m, "vs last month", "100", Percent, "new this month");

        direction.Should().Be(KpiDeltaDirection.Up);
        text.Should().Be("+12% · vs last month");
        title.Should().Be("100");
    }

    [Fact]
    public void AmountDelta_Down_RendersTrueMinus()
    {
        var (direction, text, _) =
            KpiDeltaFormatter.AmountDelta(90m, 100m, "vs last month", "100", Percent, "new this month");

        direction.Should().Be(KpiDeltaDirection.Down);
        text.Should().Be("\u221210% · vs last month");
    }

    [Fact]
    public void AmountDelta_PreviousZero_RendersNewLabel()
    {
        // A percentage against a zero base is undefined — the localized
        // "new this month" label takes over.
        var (direction, text, title) =
            KpiDeltaFormatter.AmountDelta(50m, 0m, "vs last month", "0", Percent, "new this month");

        direction.Should().Be(KpiDeltaDirection.Up);
        text.Should().Be("new this month");
        title.Should().Be("0");
    }

    [Fact]
    public void AmountDelta_Equal_HidesTheChip()
    {
        var (direction, text, title) =
            KpiDeltaFormatter.AmountDelta(100m, 100m, "vs last month", "100", Percent, "new this month");

        direction.Should().Be(KpiDeltaDirection.Flat);
        text.Should().BeEmpty();
        title.Should().BeNull();
    }

    [Fact]
    public void AmountDelta_BothZero_HidesTheChip()
    {
        var (_, text, _) =
            KpiDeltaFormatter.AmountDelta(0m, 0m, "vs last month", "0", Percent, "new this month");

        text.Should().BeEmpty();
    }

    [Fact]
    public void AmountDelta_FormattersAreUsed_ForCultureAwareRendering()
    {
        // The formatters are injected so fa-IR renders Persian digits;
        // the formatter must actually CALL them (not format inline).
        static string PersianDigits(int n)
        {
            // Minimal Persian-digit mapper for the digits this test uses.
            var s = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return s.Replace('0', '۰').Replace('1', '۱').Replace('2', '۲');
        }

        var (_, text, title) =
            KpiDeltaFormatter.CountDelta(12, 10, "نسبت به دیروز", PersianDigits);

        // diff=2 and previous=10 must BOTH render through the injected
        // formatter (Persian digits), proving the formatter delegates
        // rather than formatting inline.
        text.Should().Contain("+۲ · نسبت به دیروز");
        title.Should().Contain("۱۰");
    }
}

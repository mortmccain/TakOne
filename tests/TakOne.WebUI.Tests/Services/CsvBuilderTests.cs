using System.Globalization;
using FluentAssertions;
using TakOne.WebUI.Services;
using Xunit;

namespace TakOne.WebUI.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CsvBuilder"/> — the RFC 4180 engine behind
/// the Round 3 grid exports (Sales / AdminProducts / AdminUsers).
///
/// These tests exist because CSV escaping is where exports silently
/// corrupt: a comma in a product name, a quote in a customer name, or a
/// culture-sensitive decimal separator each produce a file Excel
/// mis-parses. The builder centralizes the rules; these tests lock them
/// in.
/// </summary>
public class CsvBuilderTests
{
    // ── Structure ───────────────────────────────────────────────────────

    [Fact]
    public void HeaderAndRows_AreCrlfSeparated()
    {
        // RFC 4180's canonical record separator — also what Excel expects.
        var csv = new CsvBuilder()
            .AddHeader("A", "B")
            .AddRow(1, 2)
            .AddRow(3, 4)
            .ToString();

        csv.Should().Be("A,B\r\n1,2\r\n3,4\r\n");
    }

    [Fact]
    public void Fields_AreCommaSeparated()
    {
        var csv = new CsvBuilder()
            .AddRow("x", "y", "z")
            .ToString();

        csv.Should().Be("x,y,z\r\n");
    }

    // ── Escaping (RFC 4180) ─────────────────────────────────────────────

    [Theory]
    [InlineData("plain")]          // no quoting needed
    [InlineData("with space")]     // spaces do NOT trigger quoting
    [InlineData("تاریخ ثبت")]       // non-ASCII passes through unquoted
    public void PlainFields_AreNotQuoted(string field)
    {
        var csv = new CsvBuilder().AddRow(field).ToString();

        csv.Should().NotContain("\"");
    }

    [Fact]
    public void FieldWithComma_IsQuoted()
    {
        var csv = new CsvBuilder().AddRow("Widgets, large").ToString();

        csv.Should().Be("\"Widgets, large\"\r\n");
    }

    [Fact]
    public void FieldWithDoubleQuote_DoublesTheQuoteAndWraps()
    {
        // The RFC's escape: " → "" inside a quoted field.
        var csv = new CsvBuilder().AddRow("Say \"hello\"").ToString();

        csv.Should().Be("\"Say \"\"hello\"\"\"\r\n");
    }

    [Fact]
    public void FieldWithNewline_IsQuoted()
    {
        // A raw newline inside a field would otherwise split the record.
        var csv = new CsvBuilder().AddRow("line1\nline2").ToString();

        csv.Should().Be("\"line1\nline2\"\r\n");
    }

    [Fact]
    public void MixedRow_EscapesOnlyTheFieldsThatNeedIt()
    {
        var csv = new CsvBuilder()
            .AddRow("plain", "with,comma", 42, "quote\"inside")
            .ToString();

        csv.Should().Be("plain,\"with,comma\",42,\"quote\"\"inside\"\r\n");
    }

    // ── Value rendering ─────────────────────────────────────────────────

    [Fact]
    public void Null_RendersAsEmptyField()
    {
        var csv = new CsvBuilder().AddRow("a", null, "c").ToString();

        csv.Should().Be("a,,c\r\n");
    }

    [Fact]
    public void Decimal_RendersInvariantWithoutGrouping()
    {
        // Persian-format "۱٫۵" or German "1,5" would corrupt the file —
        // the export must be locale-independent ASCII.
        var csv = new CsvBuilder().AddRow(1.5m, 1234.5678m).ToString();

        csv.Should().Be("1.5,1234.5678\r\n");
    }

    [Fact]
    public void Decimal_TrimsTrailingZeros()
    {
        var csv = new CsvBuilder().AddRow(100.00m).ToString();

        csv.Should().Be("100\r\n");
    }

    [Fact]
    public void Integer_RendersInvariant()
    {
        var csv = new CsvBuilder().AddRow(42).ToString();

        csv.Should().Be("42\r\n");
    }

    [Fact]
    public void Bool_RendersLowercase()
    {
        var csv = new CsvBuilder().AddRow(true, false).ToString();

        csv.Should().Be("true,false\r\n");
    }

    [Fact]
    public void DateTime_RendersSortableUtcFormat()
    {
        // ISO-8601 "u": sortable, timezone-explicit, unambiguous across
        // locales — the right shape for a data-processing export.
        var stamp = new DateTime(2026, 8, 29, 14, 30, 5, DateTimeKind.Utc);
        var csv = new CsvBuilder().AddRow(stamp).ToString();

        csv.Should().Be("2026-08-29 14:30:05Z\r\n");
    }

    // ── Culture independence ────────────────────────────────────────────

    [Theory]
    [InlineData("fa-IR")] // Persian digits + ٫ decimal separator
    [InlineData("de-DE")] // comma decimal separator
    [InlineData("en-US")]
    public void NumberRendering_IsCultureIndependent(string cultureName)
    {
        // The builder must produce the SAME bytes no matter what
        // CurrentCulture the circuit happens to run under (a fa-IR user
        // and an en-US user export the same values identically).
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            var csv = new CsvBuilder().AddRow(1234.5m).ToString();
            csv.Should().Be("1234.5\r\n");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── Fluent API ──────────────────────────────────────────────────────

    [Fact]
    public void AddHeaderAndAddRow_ReturnTheBuilderForFluentChaining()
    {
        var builder = new CsvBuilder();

        builder.AddHeader("A").Should().BeSameAs(builder);
        builder.AddRow(1).Should().BeSameAs(builder);
    }
}

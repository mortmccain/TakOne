using System.Globalization;
using System.Text;

namespace TakOne.WebUI.Services;

/// <summary>
/// RFC 4180-compliant CSV builder for the admin grid exports
/// (Sales / AdminProducts / AdminUsers — Round 3).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A BUILDER (not string.Join)</b>: CSV has deceptively tricky
/// escaping rules; getting them wrong corrupts exports the moment a
/// product name contains a comma or a customer name contains a quote.
/// Centralizing the rules here means every export site inherits correct,
/// tested behavior.
/// </para>
/// <para>
/// <b>ENCODING RULES (RFC 4180)</b>:
/// <list type="bullet">
///   <item>A field is quoted iff it contains a comma, a double quote, a
///         CR, or an LF.</item>
///   <item>Inside quotes, every double quote is doubled
///         (<c>"</c> → <c>""</c>).</item>
///   <item>CRLF separates records (the RFC's canonical line break — also
///         what Excel expects).</item>
/// </list>
/// </para>
/// <para>
/// <b>UTF-8 + BOM</b>: the JS download helper (download.js's
/// <c>takDownload.csv</c>) prepends the BOM at save time so Excel detects
/// UTF-8 and renders Persian text correctly — the BOM is deliberately NOT
/// part of this builder's output (unit tests and other consumers get clean
/// text).
/// </para>
/// <para>
/// <b>NUMBER FORMAT</b>: numbers are rendered with
/// <see cref="CultureInfo.InvariantCulture"/> (plain ASCII digits, "."
/// decimal separator) so the file round-trips through any locale's Excel
/// without 1,234.5-vs-1.234,5 corruption. Money amounts export as RAW
/// amounts (no Toman division, no Persian digits) — the export is for
/// data processing; the on-screen rendering stays culture-aware.
/// </para>
/// <para>
/// <b>THREAD SAFETY</b>: instances are NOT thread-safe (a single
/// <see cref="StringBuilder"/> accumulates state). Create one per export —
/// they're cheap.
/// </para>
/// </remarks>
public sealed class CsvBuilder
{
    // Invariant culture: the export must be locale-independent (see class
    // remarks). Persian-digit rendering stays a UI concern, not a data
    // concern.
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private readonly StringBuilder _sb = new();

    /// <summary>
    /// Writes the header row from localized column captions.
    /// Headers ARE localized (the user reads them); data values below use
    /// culture-invariant formatting.
    /// </summary>
    public CsvBuilder AddHeader(params string[] columns)
    {
        AddRow(columns);
        return this;
    }

    /// <summary>
    /// Writes one data record. <c>null</c> values render as empty fields;
    /// <see cref="DateTime"/> values render as ISO-8601 (sortable,
    /// timezone-explicit "u" format — unambiguous across locales);
    /// numbers render invariant.
    /// </summary>
    public CsvBuilder AddRow(params object?[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                _sb.Append(',');
            }

            AppendField(fields[i]);
        }

        _sb.Append("\r\n");
        return this;
    }

    /// <summary>
    /// Returns the CSV document (WITHOUT the UTF-8 BOM — see class remarks).
    /// </summary>
    public override string ToString() => _sb.ToString();

    // ── Field rendering ─────────────────────────────────────────────────

    private void AppendField(object? value)
    {
        var text = Render(value);
        _sb.Append(Escape(text));
    }

    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        DateTime dt => dt.ToString("u", Invariant),          // 2026-08-29 12:34:56Z
        DateTimeOffset dto => dto.ToUniversalTime().ToString("u", Invariant),
        decimal d => d.ToString("0.####", Invariant),        // no trailing zeros, no grouping
        double d => d.ToString("0.####", Invariant),
        float f => f.ToString("0.####", Invariant),
        int i => i.ToString(Invariant),
        long l => l.ToString(Invariant),
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? string.Empty
    };

    /// <summary>
    /// Quotes the field iff it contains a comma, quote, CR, or LF (RFC 4180
    /// minimal quoting — unquoted fields stay readable in a text editor).
    /// </summary>
    private static string Escape(string field)
    {
        var needsQuoting = field.IndexOfAny(QuoteTriggers) >= 0;
        if (!needsQuoting)
        {
            return field;
        }

        // Escape + wrap in one pass: " → "" inside a leading/trailing ".
        return "\"" + field.Replace("\"", "\"\"") + "\"";
    }

    private static readonly char[] QuoteTriggers = { ',', '"', '\r', '\n' };
}

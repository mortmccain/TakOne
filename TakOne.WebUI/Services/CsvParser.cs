using System.Globalization;
using System.Text;

namespace TakOne.WebUI.Services;

/// <summary>
/// RFC 4180-compliant CSV parser — the read-side counterpart of
/// <see cref="CsvBuilder"/> (the write side). Used by the Settings page's
/// product and user CSV imports.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A DEDICATED PARSER (not string.Split)</b>: the export quotes any
/// field containing a comma, quote, CR, or LF — so product names like
/// <c>Pasta, 500g</c> or full names containing quotes round-trip correctly
/// only through a real RFC 4180 state machine. <c>Split(',')</c> would
/// silently corrupt every such row.
/// </para>
/// <para>
/// <b>SUPPORTED GRAMMAR (RFC 4180, extended)</b>:
/// <list type="bullet">
///   <item>Fields separated by <c>,</c>; records by CRLF, lone CR, or lone
///         LF (the extension — the canonical rule is CRLF-only, but
///         hand-edited files on Linux/macOS arrive LF-only).</item>
///   <item>A field wrapped in <c>"…"</c> may contain commas, quotes
///         (doubled: <c>""</c>), and line breaks.</item>
///   <item>A quote inside an UNQUOTED field is taken literally (lenient —
///         strict RFC 4180 calls it malformed; Excel and every common
///         reader accept it).</item>
///   <item>A trailing record without a final line break is returned
///         (files written by <see cref="CsvBuilder"/> always end with CRLF,
///         but hand-edited files often don't).</item>
///   <item>An entirely empty final line is ignored (the classic
///         trailing-newline artifact).</item>
/// </list>
/// </para>
/// <para>
/// <b>BOM + WHITESPACE</b>: a UTF-8 BOM at the start of the FIRST field is
/// stripped (the export's JS download helper adds one for Excel, and a
/// round-tripped file must re-import cleanly). Field values are NOT
/// trimmed — the data (e.g. a product name with trailing spaces) is the
/// data; only the BOM is special-cased.
/// </para>
/// <para>
/// <b>NUMBER PARSING</b>: callers parse numbers with
/// <see cref="CultureInfo.InvariantCulture"/> — the same culture the
/// export writes. No locale sniffing, no auto-detection: a CSV that
/// round-tripped through this system is invariant by construction.
/// </para>
/// <para>
/// <b>THREAD SAFETY</b>: stateless static parsing — safe to call from
/// anywhere.
/// </para>
/// </remarks>
public static class CsvParser
{
    /// <summary>
    /// Parses a CSV document into records of raw field strings. Returns
    /// one list of fields per record, in file order, including the header
    /// row (callers decide how to treat it — the imports are positional
    /// and simply skip row 0).
    /// </summary>
    /// <param name="csv">The full CSV text (with or without BOM).</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="csv"/> is null.
    /// </exception>
    public static IReadOnlyList<string[]> Parse(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);

        // Strip a leading UTF-8 BOM (decoded as U+FEFF): the export's JS
        // download helper prepends one for Excel, and a round-tripped file
        // must re-import cleanly instead of growing an invisible character
        // onto the first header cell.
        if (csv.Length > 0 && csv[0] == '\uFEFF')
        {
            csv = csv[1..];
        }

        var records = new List<string[]>();
        var field = new StringBuilder();
        var fields = new List<string>();
        var inQuotes = false;
        var started = false; // any char consumed for the current record

        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Doubled quote inside a quoted field = literal quote;
                    // a lone quote closes the field.
                    if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    // Opening quote (mid-field quotes are taken literally —
                    // see the class remarks for the lenient rationale).
                    inQuotes = true;
                    started = true;
                    break;

                case ',':
                    FlushField();
                    break;

                case '\r':
                case '\n':
                    // CRLF counts as ONE record break (don't emit an empty
                    // record for the LF of a CRLF pair).
                    if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                    {
                        i++;
                    }

                    FlushRecord();
                    break;

                default:
                    field.Append(c);
                    break;
            }
        }

        // Final record without a trailing line break.
        if (inQuotes)
        {
            // Unterminated quote — emit what we have rather than throwing;
            // the caller's per-row validation reports the malformed row.
            FlushRecord();
        }
        else if (started || field.Length > 0 || fields.Count > 0)
        {
            FlushRecord();
        }

        return records;

        // Appends the current field to the record's field list.
        void FlushField()
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        // Closes the current record (if it has any content) and starts a
        // new one. An entirely empty final line produces no record.
        void FlushRecord()
        {
            if (field.Length > 0 || fields.Count > 0)
            {
                FlushField();
                records.Add(fields.ToArray());
                fields.Clear();
            }

            started = false;
        }
    }

    /// <summary>
    /// Parses a decimal with <see cref="CultureInfo.InvariantCulture"/> —
    /// the culture the CSV export always writes. Returns false for null/
    /// empty/unparseable values instead of throwing (import rows report
    /// per-row errors; exceptions would just be caught and re-shaped).
    /// </summary>
    public static bool TryParseInvariantDecimal(string? value, out decimal result)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);

    /// <summary>
    /// Parses an integer with <see cref="CultureInfo.InvariantCulture"/>.
    /// Same non-throwing contract as
    /// <see cref="TryParseInvariantDecimal(string?, out decimal)"/>.
    /// </summary>
    public static bool TryParseInvariantInt(string? value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
}

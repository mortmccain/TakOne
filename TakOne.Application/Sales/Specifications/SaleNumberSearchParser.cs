namespace TakOne.Application.Sales.Specifications;

/// <summary>
/// Parses a free-text search term against the sale-number's
/// <b>server-side reality</b> (Round 4 — server-driven paging).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A PARSER (not a SQL Contains)</b>: the human-readable sale
/// number <c>INT-۱۴۰۵-۰۰۴۲</c> is <see cref="Domain.Sales.ValueObjects.SaleNumber.Value"/>'s
/// computed-on-access display string with Persian digits — EF maps ONLY
/// the integer parts (<c>SaleNumber.Year</c>, <c>SaleNumber.Sequence</c>)
/// to columns. A <c>Contains</c> on the display string is therefore NOT
/// translatable to SQL; instead we parse the term into the integer parts
/// and emit integer predicates (translatable on every provider).
/// </para>
/// <para>
/// <b>SUPPORTED TERM SHAPES</b> (case-insensitive; Persian ۰-۹ and
/// Arabic-Indic ٠-٩ digits normalized to Latin first):
/// <list type="bullet">
///   <item><c>INT-1405-42</c> → Year == 1405 AND Sequence == 42 (the
///   pasted full number — leading zeros and zero-padding are
///   irrelevant since we compare integers).</item>
///   <item><c>INT-1405</c> → Year == 1405 (all sales of that Persian
///   year).</item>
///   <item><c>INT</c> → every sale that HAS a number (drafts
///   excluded).</item>
///   <item><c>DRAFT</c> (or any term containing "draft") → every DRAFT
///   (SaleNumber IS NULL). The pre-Round-4 in-memory search also
///   matched the Guid prefix of a specific draft's pseudo-id; that
///   exact-prefix capability is intentionally NOT carried over — the
///   keyword-level match plus the other filters covers the realistic
///   "show me my drafts" use case, and no Guid→string SQL translation
///   risk is taken.</item>
///   <item><c>42</c> (a bare integer) → Sequence == 42 OR Year == 42 —
///   a superset match, so "find sale #42" and "filter by year 1405"
///   both find what the user meant regardless of intent.</item>
///   <item>Anything else → matches nothing (the same honest behavior
///   as the old in-memory Contains against Persian-digit strings,
///   which never matched stray Latin text either).</item>
/// </list>
/// </para>
/// </remarks>
internal static class SaleNumberSearchParser
{
    /// <summary>
    /// Persian-Indic digits ۰..۹ (U+06F0..U+06F9) and Arabic-Indic
    /// digits ٠..٩ (U+0660..U+0669) mapped to their Latin counterparts.
    /// </summary>
    private static readonly char[] PersianDigits =
        { '۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹' };

    private static readonly char[] ArabicIndicDigits =
        { '٠', '١', '٢', '٣', '٤', '٥', '٦', '٧', '٨', '٩' };

    /// <summary>
    /// The parsed, SQL-expressible intent of a sale-number search term.
    /// All members are "no clause" when unset, except
    /// <see cref="MatchNothing"/> which short-circuits to an empty
    /// result (an unparseable term must not silently widen to "all
    /// rows" — the user explicitly typed a filter).
    /// </summary>
    /// <param name="Year">Exact Persian-year match, if parsed.</param>
    /// <param name="Sequence">Exact sequence match, if parsed.</param>
    /// <param name="DraftsOnly">Match only drafts (NULL SaleNumber).</param>
    /// <param name="NumberedOnly">Match only submitted sales (non-NULL
    /// SaleNumber) — set for a bare "INT" prefix.</param>
    /// <param name="SequenceOrYear">A bare-integer term: match
    /// Sequence == value OR Year == value (superset semantics).</param>
    /// <param name="MatchNothing">Unparseable term — match no rows.</param>
    public sealed record Parsed(
        int? Year,
        int? Sequence,
        bool DraftsOnly,
        bool NumberedOnly,
        int? SequenceOrYear,
        bool MatchNothing);

    /// <summary>
    /// Parses a raw search term. A null/whitespace term parses to the
    /// all-clear (no clauses) — the caller decides whether that means
    /// "no filter" (SearchTerm absent) and skips entirely.
    /// </summary>
    public static Parsed Parse(string? term)
    {
        var normalized = Normalize(term);
        if (normalized.Length == 0)
        {
            return new Parsed(null, null, false, false, null, false);
        }

        // "draft" anywhere → drafts only (checked first: the most
        // intentional of the keyword matches).
        if (normalized.Contains("draft", StringComparison.OrdinalIgnoreCase))
        {
            return new Parsed(null, null, DraftsOnly: true, false, null, false);
        }

        var lower = normalized.ToLowerInvariant();

        // "int..." prefix → the sale-number shape.
        if (lower.StartsWith(SaleNumberPrefix, StringComparison.Ordinal))
        {
            var remainder = normalized[SaleNumberPrefix.Length..].TrimStart('-', ' ');
            if (remainder.Length == 0)
            {
                // Bare "INT" → every numbered sale.
                return new Parsed(null, null, false, NumberedOnly: true, null, false);
            }

            // "1405-42" or "1405".
            var parts = remainder.Split('-', StringSplitOptions.TrimEntries);
            int? parsedYear = null;
            int? parsedSequence = null;
            if (parts.Length is >= 1 and <= 2 &&
                int.TryParse(parts[0], out var year))
            {
                parsedYear = year;
                if (parts.Length == 2 && int.TryParse(parts[1], out var sequence))
                {
                    parsedSequence = sequence;
                }
                else if (parts.Length == 2)
                {
                    // "INT-1405-abc" — the sequence part isn't an integer.
                    parsedYear = null;
                }
            }

            if (parsedYear.HasValue)
            {
                // Range-validate the way SaleNumber.Create would; a
                // year/sequence outside the representable range can
                // never match a stored row.
                var yearOk = parsedYear is >= 1300 and <= 1500;
                var seqOk = parsedSequence is null ||
                            (parsedSequence >= 1 && parsedSequence <= 99999999);
                if (yearOk && seqOk)
                {
                    return new Parsed(parsedYear, parsedSequence, false, false, null, false);
                }
            }

            // "INT-" followed by garbage → matches nothing.
            return new Parsed(null, null, false, false, null, MatchNothing: true);
        }

        // Bare integer → sequence-or-year superset match.
        if (int.TryParse(normalized, out var bare) && bare is >= 1 and <= 99999999)
        {
            return new Parsed(null, null, false, false, SequenceOrYear: bare, false);
        }

        // Anything else (free text) → matches nothing.
        return new Parsed(null, null, false, false, null, MatchNothing: true);
    }

    private const string SaleNumberPrefix = "int";

    /// <summary>
    /// Trims and normalizes Persian/Arabic-Indic digits to Latin so the
    /// integer parsers and prefix comparisons work regardless of which
    /// keyboard the user typed on.
    /// </summary>
    private static string Normalize(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return string.Empty;
        }

        var trimmed = term.Trim();
        var chars = trimmed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var persianIndex = Array.IndexOf(PersianDigits, chars[i]);
            if (persianIndex >= 0)
            {
                chars[i] = (char)('0' + persianIndex);
                continue;
            }

            var arabicIndex = Array.IndexOf(ArabicIndicDigits, chars[i]);
            if (arabicIndex >= 0)
            {
                chars[i] = (char)('0' + arabicIndex);
            }
        }

        return new string(chars);
    }
}

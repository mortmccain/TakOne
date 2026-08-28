using System.Globalization;
using System.Text;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Sales.ValueObjects;

/// <summary>
/// A globally-unique, human-readable identifier for a Sale.
///
/// FORMAT:
///   <c>INT-{PersianYear}-{Sequence:00000000}</c>
///   where:
///     - <c>INT</c> is a fixed prefix meaning "Internal".
///     - <c>{PersianYear}</c> is the 4-digit Persian (Jalali) calendar year,
///       rendered with Persian digits (e.g. <c>۱۴۰۵</c>, not <c>1405</c>).
///     - <c>{Sequence:00000000}</c> is an 8-digit, zero-padded, GLOBALLY-UNIQUE
///       sequence number representing the Nth sale of that Persian year
///       across ALL customers. Rendered with Persian digits (e.g. <c>۰۰۰۰۰۰۴۲</c>).
///
///   EXAMPLES:
///     <c>INT-۱۴۰۵-۰۰۰۰۰۰۰۱</c>  — the very first sale of Persian year 1405 (any customer)
///     <c>INT-۱۴۰۵-۰۰۰۰۰۰۴۲</c>  — the 42nd sale of Persian year 1405 (any customer)
///     <c>INT-۱۴۰۶-۰۰۰۰۰۰۰۱</c>  — the first sale of the next Persian year
///
///   UNIQUENESS:
///   The (Year, Sequence) pair is globally unique — no two sales can share
///   the same SaleNumber. The Infrastructure layer enforces this with a
///   composite unique index on the Sale table. This means:
///     - A sales employee can find a sale by SaleNumber alone, without also
///       needing the customer's worker ID.
///     - Audit logs, accounting exports, shipping labels, payment-gateway
///       references, and any external system that ingests a SaleNumber can
///       treat it as an unambiguous identifier.
///     - A customer who references "INT-۱۴۰۵-۰۰۴۲" on a phone call, in an
///       email, or on a ticket unambiguously identifies exactly one sale.
///
/// STORAGE:
///   - The Sale's <c>CreatedAtUtc</c> is stored as a standard UTC DateTime
///     (Gregorian) — no Persian calendar in the database.
///   - The SaleNumber's <c>Year</c> is the Persian year as a plain int
///     (e.g. <c>1405</c>) — stored as an int column. The Persian DIGIT
///     rendering (۱۴۰۵) only happens in the <see cref="Value"/> string.
///   - <c>Sequence</c> is a plain int (e.g. <c>42</c>).
///   - <c>Value</c> is the canonical display string with Persian digits.
///
/// WHO COMPUTES THE PARTS:
///   - The Persian year and the global sequence are computed by
///     <c>ISaleNumberGenerator</c> in the Infrastructure layer, which has
///     access to <c>System.Globalization.PersianCalendar</c> and the database
///     (to count all prior sales in the current Persian year).
///   - This value object is constructed via <see cref="Create"/> with the
///     already-computed parts. It does NOT do calendar math itself, so the
///     Domain layer stays free of culture/framework dependencies.
///
/// RACE CONDITION (concurrent sale creation):
///   If two requests race, both might compute the same sequence number.
///   This is acceptable — the Infrastructure layer's unique index on
///   (Year, Sequence) causes the loser's SaveChangesAsync to fail with a
///   unique-constraint violation, which the handler can retry.
/// </summary>
public class SaleNumber : BaseValueObject
{



    // ==================================================================================================================================
    //                                                          CONSTANTS
    // ==================================================================================================================================



    /// <summary>
    /// Fixed prefix for all SaleNumbers. "INT" = "Internal".
    /// Centralized here so it can never drift out of sync across call sites.
    /// </summary>
    public const string Prefix = "INT";

    /// <summary>
    /// Minimum and maximum Persian year we'll accept. Persian year 1300
    /// corresponds roughly to Gregorian 1921; 1500 corresponds to roughly
    /// 2121. This guard catches obvious garbage without being too tight.
    /// </summary>
    public const int MinPersianYear = 1300;
    public const int MaxPersianYear = 1500;

    /// <summary>
    /// Sequence bounds: 1 to 99999999. The 8-digit format (D8) requires this range.
    /// 99,999,999 sales per Persian year is ~274,000 sales per day, which is far
    /// beyond any plausible B2B/retail capacity. The 8-digit width also aligns
    /// with typical ERP numbering (SAP, Oracle, NetSuite all use 8-digit sequences).
    /// If a deployment ever exceeds this, the column type (int) still holds up
    /// to 2,147,483,647 — but we'd cross that bridge when we get to it.
    /// </summary>
    public const int MinSequence = 1;
    public const int MaxSequence = 99999999;



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    /// <summary>
    /// The Persian (Jalali) calendar year, as a plain int (e.g. <c>1405</c>).
    /// Stored as Latin digits in the database; the Persian-digit rendering
    /// only appears in <see cref="Value"/>.
    /// </summary>
    public int Year { get; }

    /// <summary>
    /// The global sequence number for this Persian year (1..99999999).
    /// A value of <c>42</c> means this is the 42nd sale created across ALL
    /// customers in year <see cref="Year"/>.
    /// </summary>
    public int Sequence { get; }

    /// <summary>
    /// The canonical display string with Persian digits, e.g. <c>INT-۱۴۰۵-۰۰۴۲</c>.
    /// This is what users see in the UI and what audit logs print.
    ///
    /// IMPLEMENTATION NOTE:
    ///   This is an expression-bodied property (computed on every access), NOT
    ///   a cached field. Reason: EF Core materializes value objects via their
    ///   parameterless constructor, which would leave a cached field null.
    ///   Computing on access guarantees <c>Value</c> is always correct after
    ///   EF sets <see cref="Year"/> and <see cref="Sequence"/>. The CPU cost
    ///   of formatting two short ints is negligible.
    /// </summary>
    public string Value =>
        $"{Prefix}-{ToPersianDigits(Year.ToString(CultureInfo.InvariantCulture))}-{ToPersianDigits(Sequence.ToString("D8", CultureInfo.InvariantCulture))}";



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor.
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in application code.
    /// </summary>
    private SaleNumber() { }
#pragma warning restore CS8618

    /// <summary>
    /// Private constructor used by the static factory method.
    /// </summary>
    private SaleNumber(int persianYear, int sequence)
    {
        EnsureYearValid(persianYear);
        EnsureSequenceValid(sequence);

        Year = persianYear;
        Sequence = sequence;
    }



    // ==================================================================================================================================
    //                                                          FACTORY METHOD
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new SaleNumber from its parts. This is the ONLY way to
    /// construct a SaleNumber from application code.
    ///
    /// The parts are computed by <c>ISaleNumberGenerator</c> (Infrastructure),
    /// which uses <c>System.Globalization.PersianCalendar</c> for the year
    /// and counts all prior sales in the current Persian year for the sequence.
    /// </summary>
    public static SaleNumber Create(int persianYear, int sequence)
    {
        return new SaleNumber(persianYear, sequence);
    }



    // ==================================================================================================================================
    //                                                          PERSIAN DIGIT HELPER
    // ==================================================================================================================================



    /// <summary>
    /// Converts any Latin (ASCII) digits in the input string to Persian digits.
    /// Non-digit characters are passed through unchanged.
    ///
    /// The mapping is:
    ///   '0' → '۰' (U+06F0)
    ///   '1' → '۱' (U+06F1)
    ///   ...
    ///   '9' → '۹' (U+06F9)
    ///
    /// This is a deliberate, self-contained mapping rather than a CultureInfo
    /// call, so the Domain layer has no dependency on framework localization.
    /// It also guarantees the same output regardless of server culture settings.
    /// </summary>
    private static string ToPersianDigits(string value)
    {
        // Persian-Indic digits ۰..۹ live at U+06F0..U+06F9 as a contiguous block.
        // We can map by index instead of a switch for clarity and speed.
        var persianDigits = new[]
        {
            '۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹'
        };

        var result = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            // `char.IsDigit` returns true for any Unicode digit (Latin, Persian,
            // Arabic-Indic, Thai, etc.). We only want to remap Latin '0'..'9'.
            // The fastest, unambiguous check is the ASCII range comparison.
            if (c >= '0' && c <= '9')
            {
                result.Append(persianDigits[c - '0']);
            }
            else
            {
                result.Append(c);
            }
        }
        return result.ToString();
    }



    // ==================================================================================================================================
    //                                                          GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureYearValid(int persianYear)
    {
        if (persianYear < MinPersianYear || persianYear > MaxPersianYear)
        {
            throw new ArgumentException(
                $"Persian year {persianYear} is out of the supported range " +
                $"[{MinPersianYear}, {MaxPersianYear}].", nameof(persianYear));
        }
    }

    private static void EnsureSequenceValid(int sequence)
    {
        if (sequence < MinSequence || sequence > MaxSequence)
        {
            throw new ArgumentException(
                $"Sale sequence {sequence} is out of the supported range " +
                $"[{MinSequence}, {MaxSequence}]. The system cannot create " +
                $"more than {MaxSequence} sales in one Persian year under " +
                $"the current 8-digit format.", nameof(sequence));
        }
    }



    // ==================================================================================================================================
    //                                                          VALUE OBJECT INFRASTRUCTURE
    // ==================================================================================================================================



    public override string ToString() => Value;

    protected override IEnumerable<object> GetEqualityComponents()
    {
        // Two SaleNumbers are equal iff Year AND Sequence match. The prefix
        // is a constant so it doesn't participate in equality — including it
        // would be redundant.
        yield return Year;
        yield return Sequence;
    }
}
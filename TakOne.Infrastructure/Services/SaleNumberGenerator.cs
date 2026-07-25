using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Infrastructure.Persistence;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Infrastructure implementation of <see cref="ISaleNumberGenerator"/>.
///
/// ALGORITHM:
///   1. Take <c>DateTime.UtcNow</c> (the Gregorian "now").
///   2. Convert it to a Persian (Jalali) year via
///      <see cref="PersianCalendar.GetYear"/>. (The PersianCalendar class is
///      thread-safe and lives in <c>System.Globalization</c>.)
///   3. Compute the Gregorian DateTime that corresponds to the START of that
///      Persian year — i.e. the Persian New Year (Nowruz, around March 20/21).
///      We do this by calling
///      <c>_persianCalendar.ToDateTime(year, 1, 1, 0, 0, 0, 0)</c>.
///   4. Count ALL existing Sales whose <c>CreatedAtUtc &gt;= persianYearStart</c>.
///      This is the GLOBAL sequence counter — not per-customer, not per-anything.
///   5. Return <c>SaleNumber.Create(persianYear, count + 1)</c>.
///
/// WHY "COUNT ALL SALES" NOT "MAX(SEQUENCE) + 1":
///   We could in theory read <c>MAX(SaleNumber_Sequence)</c> for the current
///   Persian year and add 1. That would be faster on a huge table (one indexed
///   aggregate vs one filtered count). But it has a subtle bug: if the latest
///   sale in the year is HARD-DELETED (which is allowed for Draft sales via
///   <c>ISaleRepository.DeleteAsync</c>), MAX would return the previous sale's
///   sequence, and we'd reuse a sequence number — violating the
///   "globally unique, never reused" contract.
///
///   Counting ALL sales in the year means: even if sale #42 is hard-deleted,
///   the next sale is #43 (because we count the 42 surviving sales + 1 = 43),
///   never reusing #42. Deleted sequence numbers become permanently retired.
///   This is the correct audit-friendly behavior.
///
///   Trade-off: COUNT(*) on an indexed column is fast on SQL Server even for
///   tens of millions of rows — the year filter prunes to a few thousand at
///   most. If this ever becomes a bottleneck, we'd add a dedicated
///   SaleSequenceCounter table (one row per Persian year) and use SELECT...
///   FOR UPDATE / SERIALIZABLE isolation to allocate the next sequence — but
///   that's premature optimization today.
///
/// CONCURRENCY (the race condition):
///   Two simultaneous CreateSale calls can both run step 4 before either has
///   committed step 5's INSERT. Both compute the same count → both return the
///   same sequence. This is OK because:
///     - The Infrastructure layer's UNIQUE INDEX on
///       <c>(SaleNumber_Year, SaleNumber_Sequence)</c> (see SaleConfiguration)
///       causes the loser's <c>SaveChangesAsync</c> to throw a
///       <c>DbUpdateException</c> wrapping a unique-constraint violation.
///     - The handler is expected to retry (Polly policy) — TODO: not yet wired.
///       Until the retry policy is added, the loser's request fails with a
///       generic "SaleNumber collision, please retry" error.
///
///   We do NOT use a transaction with SERIALIZABLE isolation here — that
///   would serialize ALL sale creations globally, killing throughput. The
///   unique-index + retry pattern is the standard scalable solution.
///
/// TIMEZONE NOTE:
///   PersianCalendar is purely a date-arithmetic helper — it converts a
///   Gregorian DateTime to a Persian date and back. It is NOT a timezone.
///   We always work in UTC, so the "Persian year of now" is the Persian year
///   of <c>DateTime.UtcNow</c>. The Persian New Year happens at a specific
///   astronomical moment (the spring equinox), observed worldwide
///   simultaneously — so using UTC is correct, not "Tehran local time".
///
///   Edge case: at the exact moment of the equinox, "now" might still be
///   the old Persian year in UTC while it's the new Persian year in Tehran
///   local time (Tehran is UTC+3:30). We accept this — the PersianCalendar
///   class's GetYear is authoritative for our purposes, and a one-second
///   ambiguity at the year boundary is irrelevant for a sales-counter
///   sequence.
/// </summary>
public sealed class SaleNumberGenerator : ISaleNumberGenerator
{
    // PersianCalendar is documented as thread-safe for read-only use.
    // Marking readonly so we never reassign it; the same instance serves
    // all requests.
    private static readonly PersianCalendar _persianCalendar = new();

    private readonly ApplicationDbContext _db;
    private readonly ILogger<SaleNumberGenerator> _logger;

    public SaleNumberGenerator
        (
        ApplicationDbContext db,
        ILogger<SaleNumberGenerator> logger
        )
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SaleNumber> NextAsync(CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // 1. Determine the current Persian year from UTC now.
        // ------------------------------------------------------------------
        var nowUtc = DateTime.UtcNow;
        var persianYear = _persianCalendar.GetYear(nowUtc);

        // ------------------------------------------------------------------
        // 2. Compute the Gregorian DateTime that corresponds to the start of
        //    the Persian year (the 1st day of month 1, at 00:00:00).
        //
        //    PersianCalendar.ToDateTime(year, month, day, hour, minute, second, millisecond)
        //    returns a Gregorian DateTime that represents the same instant as
        //    the given Persian date. We want "Persian year-start, midnight" —
        //    i.e. PersianCalendar.ToDateTime(persianYear, 1, 1, 0, 0, 0, 0).
        //
        //    This DateTime is in the same Kind as PersianCalendar.ToDateTime
        //    returns, which is Unspecified. We convert to UTC via
        //    DateTime.SpecifyKind(..., DateTimeKind.Utc) so that EF Core
        //    stores it correctly as a datetime2 column.
        //
        //    Note: the conversion is "midnight at the start of the Persian
        //    new year, in the proleptic Gregorian calendar" — there's no
        //    timezone shift. Sales stored their CreatedAtUtc in UTC, so the
        //    comparison CreatedAtUtc >= persianYearStartUtc is correct.
        // ------------------------------------------------------------------
        var persianYearStartUtc = DateTime.SpecifyKind
            (
            _persianCalendar.ToDateTime(persianYear, 1, 1, 0, 0, 0, 0),
            DateTimeKind.Utc
            );

        // ------------------------------------------------------------------
        // 3. Count ALL sales in the current Persian year.
        //
        //    Why count all sales, not max(Sequence)+1? See class-level docs:
        //    hard-deleted Draft sales would leave gaps in the Sequence space,
        //    and MAX+1 would reuse those gap numbers — violating the
        //    "globally unique, never reused" contract. Counting the surviving
        //    sales + 1 means deleted sequence numbers stay retired.
        //
        //    The CreatedAtUtc column has a non-unique index (see
        //    SaleConfiguration), so the COUNT(*) is fast — it scans only the
        //    index entries for the current year, not the whole table.
        // ------------------------------------------------------------------
        var existingCount = await _db.Sales
            .CountAsync(s => s.CreatedAtUtc >= persianYearStartUtc, cancellationToken);

        // ------------------------------------------------------------------
        // 4. Compute the new sequence. +1 because we want the NEXT number,
        //    not the count itself. (If there are 0 sales this year, this is
        //    sale #1. If there are 41, this is sale #42.)
        //
        //    Note: if existingCount + 1 exceeds SaleNumber.MaxSequence (9999),
        //    SaleNumber.Create will throw an ArgumentException with a clear
        //    message. The CreateSale handler will surface this to the user
        //    as "system capacity reached for this year — contact support".
        //    This is the correct failure mode: we will NOT silently overflow
        //    to 5 digits.
        // ------------------------------------------------------------------
        var sequence = existingCount + 1;

        var saleNumber = SaleNumber.Create(persianYear, sequence);

        _logger.LogDebug
            (
            "SaleNumberGenerator: generated SaleNumber {SaleNumber} " +
            "(Persian year {Year}, sequence {Sequence}, UTC now {NowUtc:o}, " +
            "Persian year started {YearStartUtc:o}).",
            saleNumber.Value, persianYear, sequence, nowUtc, persianYearStartUtc
            );

        return saleNumber;
    }
}
using TakOne.Domain.Sales.ValueObjects;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Generates a globally-unique <see cref="SaleNumber"/> for a new Sale.
///
/// IMPLEMENTATION CONTRACT (fulfilled by Infrastructure's
/// <c>SaleNumberGenerator</c>):
///   1. Take the current UTC time and convert it to a Persian (Jalali) year
///      via <c>System.Globalization.PersianCalendar.GetYear(...)</c>.
///   2. Atomically allocate the next sequence number for that Persian year
///      from the <c>SaleSequenceCounters</c> table (one row per year) using
///      a serializable transaction with UPDLOCK, HOLDLOCK. This is the
///      AUTHORITATIVE source of truth — independent of the Sales table, so
///      hard-deletes of Draft sales can never cause sequence reuse or
///      collisions. See <c>SaleSequenceCounter</c> for the full rationale.
///   3. Return <c>SaleNumber.Create(persianYear, allocatedSequence)</c>.
///
/// CONCURRENCY:
///   The serializable + UPDLOCK + HOLDLOCK pattern serializes sequence
///   allocation within a Persian year at the row level. Two concurrent
///   <see cref="NextAsync"/> calls for the same year will block each other
///   for the duration of the short counter transaction (a few milliseconds)
///   and receive DISTINCT sequence numbers. No retry loop, no
///   unique-constraint violations, no burned sequence numbers from races.
///
/// WHY THE SIGNATURE TAKES NO PARAMETERS:
///   The SaleNumber prefix is fixed at <c>"INT"</c> (see <see cref="SaleNumber.Prefix"/>),
///   the Persian year is computed from the server clock, and the sequence is
///   global (not per-customer). There is nothing caller-specific to pass in.
/// </summary>
public interface ISaleNumberGenerator
{
    /// <summary>
    /// Generates the next globally-unique <see cref="SaleNumber"/>, for the
    /// current Persian year.
    /// </summary>
    /// <param name="cancellationToken">Standard cancellation token.</param>
    /// <returns>
    /// A new <see cref="SaleNumber"/> with the Persian year and global
    /// sequence already computed.
    /// </returns>
    Task<SaleNumber> NextAsync(CancellationToken cancellationToken = default);
}
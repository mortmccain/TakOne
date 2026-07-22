using TakOne.Domain.Sales.ValueObjects;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Generates a globally-unique <see cref="SaleNumber"/> for a new Sale.
///
/// IMPLEMENTATION CONTRACT (fulfilled by Infrastructure, step 7d):
///   1. Take the current UTC time and convert it to a Persian (Jalali) year
///      via <c>System.Globalization.PersianCalendar.GetYear(...)</c>.
///   2. Compute the Gregorian DateTime that corresponds to the start of that
///      Persian year (the Persian New Year, around March 20/21).
///   3. Count ALL existing sales whose <c>CreatedAtUtc</c> falls on or after
///      that Persian-year-start DateTime — across ALL customers. This is the
///      global sequence counter, not a per-customer one.
///   4. Return <c>SaleNumber.Create(persianYear, count + 1)</c>.
///
/// CONCURRENCY:
///   Two concurrent requests can both observe the same "current count" and
///   both return the same sequence number. The Infrastructure layer's unique
///   index on <c>(SaleNumber_Year, SaleNumber_Sequence)</c> causes the loser's
///   <c>SaveChangesAsync</c> to fail with a unique-constraint violation. The
///   handler is expected to retry (Polly policy) — to be added in a future
///   hardening pass.
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
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Infrastructure.Persistence;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Infrastructure implementation of <see cref="ISaleNumberGenerator"/>.
///
/// ALGORITHM (v4 — dedicated SaleSequenceCounters table with atomic
/// self-healing UPDATE...OUTPUT, on a SEPARATE SqlConnection from the
/// caller's Wolverine transaction):
///   1. Take <c>DateTime.UtcNow</c> (the Gregorian "now").
///   2. Convert it to a Persian (Jalali) year via
///      <see cref="PersianCalendar.GetYear"/>.
///   3. Open a NEW <c>SqlConnection</c> (separate from the
///      <c>ApplicationDbContext</c>'s connection) and execute an
///      atomic <c>UPDATE...OUTPUT</c> against
///      <c>SaleSequenceCounters</c>:
///      <code>
///      UPDATE SaleSequenceCounters
///      SET NextSequence =
///          CASE
///              WHEN NextSequence &lt;= MAX(Sales.SaleNumber_Sequence for the year)
///              THEN MAX(Sales.SaleNumber_Sequence for the year) + 2
///              ELSE NextSequence + 1
///          END
///      OUTPUT INSERTED.NextSequence - 1
///      WHERE Year = @persianYear;
///      </code>
///      The CASE expression is the SELF-HEALING mechanism: if the
///      counter has drifted behind MAX(Sales.SaleNumber_Sequence) for
///      the year (e.g. because a Sale was inserted by a code path that
///      doesn't update the counter), the counter is advanced to MAX+1
///      BEFORE the increment, so the OUTPUT returns MAX+1 (the next
///      free sequence) instead of a stale value that would collide
///      with an existing Sale.
///      SQL Server takes an exclusive row lock on the matched row,
///      so two concurrent calls for the same year serialize
///      automatically — no explicit transaction or locking hints
///      required for the existing-row case.
///   4. If the UPDATE affected 0 rows (no counter exists yet for this
///      Persian year), INSERT one seeded from
///      <c>MAX(Sales.SaleNumber_Sequence) + 2</c> (or 2 if no sales
///      exist for the year yet) and return
///      <c>MAX(Sales.SaleNumber_Sequence) + 1</c>. The unique index on
///      Year catches the rare first-sale-of-the-year race: the loser's
///      INSERT throws <c>SqlException.Number == 2601</c>, which we
///      catch and retry as an UPDATE (which now finds the winner's row).
///   5. Return <c>SaleNumber.Create(persianYear, allocatedSequence)</c>.
///
/// ── WHY A SEPARATE SqlConnection (THE WOLVERINE TRANSACTION FIX) ─────
///
/// Wolverine's EF Core transactional middleware wraps every command
/// handler in a transaction on the <c>ApplicationDbContext</c>'s
/// connection:
///
///   Wolverine middleware:
///     BEGIN TRANSACTION ON _db.Database
///       → handler runs
///         → SaleNumberGenerator.NextAsync() called
///           → ⚠️ if we used _db.Database.BeginTransactionAsync()
///              here, EF throws:
///              "The connection is already in a transaction and
///               cannot participate in another transaction."
///              (one connection, one transaction at a time)
///       → _db.SaveChangesAsync()
///     COMMIT TRANSACTION
///
/// The fix is to allocate the sequence on a SEPARATE SqlConnection
/// that is completely independent of the DbContext's connection. The
/// increment commits immediately (auto-commit; no explicit
/// transaction needed because the UPDATE...OUTPUT is a single
/// atomic statement).
///
/// BENEFIT — INDEPENDENT COMMIT (gap-burning semantics):
///   Because the counter increment commits on its own connection,
///   it is NOT rolled back if the Wolverine transaction rolls back.
///   This is the standard ERP behavior for document number ranges
///   (SAP, Oracle EBS, Microsoft Dynamics all work this way): if a
///   sale creation fails for any reason (validation, stock
///   exhaustion, network glitch), the allocated sequence number is
///   "burned" — no sale will ever carry it. Gaps are expected and
///   acceptable because:
///     - SaleNumbers are internal identifiers, not customer-facing
///       invoice numbers.
///     - Hard-deleted Drafts already produce gaps by definition.
///
/// ── WHY NOT USE _db (the scoped DbContext) AT ALL ───────────────────
///
///   1. The DbContext's connection is already in Wolverine's
///      transaction. Any work we do on it is part of that transaction.
///      That means:
///        - We can't begin our own transaction (the bug we just fixed).
///        - If we used a raw SQL statement on _db without our own
///          transaction, the statement would run inside Wolverine's
///          transaction — which means the increment would be rolled
///          back if the Sale insert failed. That defeats the gap-
///          burning design AND makes the increment non-atomic with
///          respect to other concurrent allocations (Wolverine's
///          default isolation is READ COMMITTED).
///   2. Using a separate SqlConnection is the only way to get truly
///      independent, atomic, immediately-committed sequence
///      allocation.
///
/// ── SELF-HEALING (counter drift recovery) ───────────────────────────
///
///   The counter can drift behind the Sales table in several real-world
///   scenarios:
///     - The migration backfill ran at time T0 against a snapshot of
///       Sales. Any Sale created between T0 and the new generator
///       taking over leaves the counter behind.
///     - A DBA manually inserted a Sale row without going through the
///       generator (data migration, support tooling, etc.).
///     - The counter row was deleted and re-created without re-running
///       the backfill.
///
///   Without self-healing, the next allocation after a drift returns a
///   sequence that already exists in Sales → the unique index rejects
///   the INSERT → the UnitOfWork retry kicks in (which works, but burns
///   CPU and logs a scary fail-level EF Core error).
///
///   The self-healing CASE expression in the UPDATE...OUTPUT checks
///   MAX(Sales.SaleNumber_Sequence) on every allocation. If the counter
///   is behind, it's advanced to MAX+1 before the increment. The cost
///   is one indexed seek per allocation (the unique index
///   IX_Sales_SaleNumber_Year_SaleNumber_Sequence supports the MAX
///   subquery as an index seek + TOP 1, not a table scan) — negligible
///   vs. the rest of the handler.
///
/// ── CONCURRENCY CORRECTNESS ──────────────────────────────────────────
///
/// Existing-row case (steady state — the only case that matters
/// after the first sale of the year):
///   - SQL Server's UPDATE statement is atomic by definition.
///   - The UPDATE acquires an exclusive (X) lock on the row for the
///     duration of the statement.
///   - Two concurrent UPDATEs for the same year: the second blocks
///     on the X lock; once the first commits, the second reads the
///     NEW NextSequence, increments it, and returns. Two distinct
///     sequence numbers guaranteed.
///   - No explicit transaction, no UPDLOCK/HOLDLOCK hints needed —
///     the UPDATE statement itself does all the locking.
///
/// First-sale-of-the-year case (rare — once per Persian year):
///   - Two concurrent calls both find no row, both attempt INSERT.
///   - The unique index on Year causes the loser's INSERT to throw
///     <c>SqlException.Number == 2601</c>.
///   - We catch the 2601 and retry as an UPDATE, which now finds
///     the winner's row and proceeds as the existing-row case.
///   - The loser's retry succeeds on the first re-attempt.
///
/// ── TIMEZONE NOTE ───────────────────────────────────────────────────
///
/// PersianCalendar is purely a date-arithmetic helper — it converts
/// a Gregorian DateTime to a Persian date and back. It is NOT a
/// timezone. We always work in UTC, so the "Persian year of now" is
/// the Persian year of <c>DateTime.UtcNow</c>. The Persian New Year
/// happens at a specific astronomical moment (the spring equinox),
/// observed worldwide simultaneously — so using UTC is correct, not
/// "Tehran local time".
/// </summary>
public sealed class SaleNumberGenerator : ISaleNumberGenerator
{
    // PersianCalendar is documented as thread-safe for read-only use.
    // Marked static readonly so we never reassign it; the same instance
    // serves all requests.
    private static readonly PersianCalendar _persianCalendar = new();

    // We inject ApplicationDbContext ONLY to read the connection string
    // via _db.Database.GetConnectionString(). We do NOT use _db for any
    // data access — that would enlist in Wolverine's ambient transaction
    // and either throw (if we tried to begin our own transaction) or
    // defeat the gap-burning design (if we ran inside Wolverine's
    // transaction).
    //
    // Alternative: inject IConfiguration and read
    // Configuration.GetConnectionString("DefaultConnection"). Both work;
    // the DbContext approach is more robust because it picks up the same
    // connection string EF Core is configured with (no risk of drift
    // between two config keys).
    private readonly ApplicationDbContext _db;
    private readonly ILogger<SaleNumberGenerator> _logger;

    public SaleNumberGenerator(
        ApplicationDbContext db,
        ILogger<SaleNumberGenerator> logger)
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
        // 2. Read the connection string from the DbContext. We do NOT use
        //    the DbContext's connection — we create a fresh SqlConnection
        //    below. See the class-level doc comment for the full rationale
        //    (the Wolverine ambient-transaction conflict).
        // ------------------------------------------------------------------
        var connectionString = _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "SaleNumberGenerator: ApplicationDbContext has no connection string " +
                "configured. Cannot allocate a SaleNumber without a database connection.");
        }

        // ------------------------------------------------------------------
        // 3. Open a SEPARATE SqlConnection. This connection is independent
        //    of the ApplicationDbContext's connection, so it does NOT
        //    participate in Wolverine's ambient transaction. The
        //    UPDATE...OUTPUT below will auto-commit on this connection,
        //    giving us the gap-burning semantics the design requires.
        // ------------------------------------------------------------------
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var allocatedSequence = await TryAllocateFromExistingRowAsync(
            connection, persianYear, cancellationToken);

        if (allocatedSequence is null)
        {
            // No row existed for this Persian year — first sale of the year.
            // Try to INSERT; if we lose a concurrent race, retry as UPDATE.
            allocatedSequence = await InsertFirstRowOrRetryUpdateAsync(
                connection, persianYear, cancellationToken);
        }

        // ------------------------------------------------------------------
        // 4. Capacity guard — matches SaleNumber.MaxSequence. If the
        //    counter somehow reached a value > MaxSequence, the year is
        //    at capacity. We throw here (after the increment has
        //    committed — that's fine, the burned sequence is acceptable)
        //    so the caller sees a clear error instead of an
        //    ArgumentOutOfRangeException from SaleNumber.Create.
        // ------------------------------------------------------------------
        if (allocatedSequence.Value > SaleNumber.MaxSequence)
        {
            throw new InvalidOperationException(
                $"Sale sequence capacity reached for Persian year {persianYear}. " +
                $"The system cannot create more than {SaleNumber.MaxSequence} sales " +
                $"in one Persian year under the current 4-digit format. " +
                $"Contact support to extend the format.");
        }

        var saleNumber = SaleNumber.Create(persianYear, allocatedSequence.Value);

        _logger.LogDebug(
            "SaleNumberGenerator: allocated SaleNumber {SaleNumber} " +
            "(Persian year {Year}, sequence {Sequence}, UTC now {NowUtc:o}).",
            saleNumber.Value, persianYear, allocatedSequence.Value, nowUtc);

        return saleNumber;
    }

    /// <summary>
    /// Attempts the atomic UPDATE...OUTPUT against an existing counter row
    /// for the given Persian year. Returns the allocated sequence number
    /// (the OLD NextSequence value, before the increment), or <c>null</c>
    /// if no row exists for that year yet.
    ///
    /// SQL Server takes an exclusive (X) lock on the matched row for the
    /// duration of the UPDATE statement. Two concurrent calls for the
    /// same year serialize automatically — the second blocks on the X
    /// lock, then reads the post-increment value and increments again.
    /// No explicit transaction or locking hints needed.
    ///
    /// ── SELF-HEALING (counter drift recovery) ──────────────────────────
    ///
    /// The UPDATE statement below does NOT just increment NextSequence
    /// blindly. It first compares the counter's current NextSequence
    /// against <c>MAX(Sales.SaleNumber_Sequence)</c> for the same year
    /// and, if the counter has drifted behind (NextSequence ≤ MAX), it
    /// advances the counter to <c>MAX + 1</c> BEFORE the increment.
    ///
    /// WHY THIS IS NEEDED:
    ///   The counter can drift behind the Sales table in several real-
    ///   world scenarios:
    ///   <list type="bullet">
    ///     <item>The migration backfill ran at time T0 against a snapshot
    ///       of Sales. Any Sale created between T0 and the new generator
    ///       taking over (e.g. by the old MAX+1 code path, by manual SQL,
    ///       or by a test seed script) leaves the counter behind.</item>
    ///     <item>A DBA manually inserted a Sale row without going through
    ///       the generator (data migration, support tooling, etc.).</item>
    ///     <item>The counter row was deleted and re-created (intentionally
    ///       or by accident) without re-running the backfill.</item>
    ///   </list>
    ///
    ///   Without self-healing, the next allocation after a drift returns
    ///   a sequence that already exists in Sales → the unique index
    ///   <c>IX_Sales_SaleNumber_Year_SaleNumber_Sequence</c> rejects the
    ///   INSERT → the UnitOfWork retry kicks in (which works, but burns
    ///   CPU and logs a scary <c>fail</c>-level EF Core error).
    ///
    /// HOW IT WORKS:
    ///   The single atomic UPDATE evaluates a CASE expression:
    ///
    ///   <code>
    ///   IF (NextSequence &lt;= MAX(Sales.SaleNumber_Sequence for the year))
    ///     -- counter is behind (or equal) — advance it to MAX + 1, then
    ///     -- the +1 from the outer SET gives MAX + 2, so the OUTPUT
    ///     -- returns (MAX + 2) - 1 = MAX + 1 (the next free sequence).
    ///     SET NextSequence = MAX + 2
    ///   ELSE
    ///     -- counter is ahead (normal case) — just increment.
    ///     SET NextSequence = NextSequence + 1
    ///   </code>
    ///
    ///   The OUTPUT clause returns <c>INSERTED.NextSequence - 1</c>, which
    ///   is the value to allocate (the OLD NextSequence, or MAX+1 if the
    ///   counter was healed).
    ///
    ///   This is a SINGLE atomic statement — no separate "read MAX, then
    ///   update" round-trip, no race window between the read and the
    ///   write. SQL Server takes an X lock on the counter row for the
    ///   duration, so concurrent allocations serialize.
    ///
    /// PERFORMANCE:
    ///   The subquery <c>SELECT MAX(SaleNumber_Sequence) FROM Sales WHERE
    ///   SaleNumber_Year = @Year</c> is supported by the same unique index
    ///   <c>IX_Sales_SaleNumber_Year_SaleNumber_Sequence</c> that catches
    ///   duplicate inserts — it's an index seek + TOP 1, not a table scan.
    ///   Cost: one indexed seek per allocation, negligible vs. the rest
    ///   of the handler (which loads products, validates stock, etc.).
    /// </summary>
    private static async Task<int?> TryAllocateFromExistingRowAsync(
        SqlConnection connection,
        int persianYear,
        CancellationToken cancellationToken)
    {
        // Self-healing UPDATE...OUTPUT:
        //
        // 1. The CASE checks whether the counter has fallen behind
        //    MAX(Sales.SaleNumber_Sequence) for the year. If so, it
        //    resets NextSequence to MAX+1 (so the next allocation will
        //    return MAX+1, the first free sequence after all existing
        //    sales).
        //
        // 2. The outer SET then adds 1 (so the stored value becomes
        //    MAX+2 in the heal case, or NextSequence+1 in the normal
        //    case).
        //
        // 3. The OUTPUT returns INSERTED.NextSequence - 1, which is the
        //    sequence to allocate (MAX+1 in the heal case, or the OLD
        //    NextSequence in the normal case).
        //
        // The ISNULL handles the (extremely unlikely) case where the
        // counter row exists but no Sales rows exist for the year yet —
        // in that case MAX returns NULL, ISNULL coerces to 0, and the
        // CASE behaves as the normal increment path.
        const string sql = @"
UPDATE SaleSequenceCounters
SET NextSequence =
    CASE
        WHEN NextSequence <= ISNULL((
            SELECT MAX(s.SaleNumber_Sequence)
            FROM Sales s
            WHERE s.SaleNumber_Year = @Year
        ), 0)
        THEN ISNULL((
            SELECT MAX(s.SaleNumber_Sequence)
            FROM Sales s
            WHERE s.SaleNumber_Year = @Year
        ), 0) + 2
        ELSE NextSequence + 1
    END
OUTPUT INSERTED.NextSequence - 1 AS AllocatedSequence
WHERE Year = @Year;";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@Year", SqlDbType.Int).Value = persianYear;

        // ExecuteScalarAsync returns the first column of the first row of
        // the result set, or null if the result set is empty.
        //
        // When the UPDATE matches 0 rows (no counter for this year yet),
        // the OUTPUT clause produces no rows → ExecuteScalar returns null.
        // When it matches 1 row (the steady state), OUTPUT produces 1 row
        // with 1 column (the allocated sequence) → ExecuteScalar returns
        // that int, boxed as object.
        var result = await cmd.ExecuteScalarAsync(cancellationToken);

        if (result is int allocated)
        {
            return allocated;
        }

        return null;
    }

    /// <summary>
    /// Handles the "first sale of the Persian year" case: no counter row
    /// exists yet, so we INSERT one. The INSERT seeds <c>NextSequence</c>
    /// from <c>MAX(Sales.SaleNumber_Sequence) + 2</c> for the year (or 2
    /// if the year has no sales yet), and we return
    /// <c>MAX(Sales.SaleNumber_Sequence) + 1</c> as the allocated sequence.
    ///
    /// WHY WE READ MAX(Sales) HERE TOO:
    ///   The "no counter row" case can fire in TWO situations:
    ///   <list type="bullet">
    ///     <item>Genuinely new Persian year (no sales yet) — MAX is NULL,
    ///       ISNULL coerces to 0, we INSERT NextSequence=2 and return 1.
    ///       This is the first-sale-of-the-year path the design intended.</item>
    ///     <item>Counter row was deleted (or never created by a missed
    ///       migration backfill) for a year that ALREADY has sales. In
    ///       this case, hardcoding NextSequence=2 would re-allocate
    ///       sequence 1, colliding with the existing sale that has
    ///       sequence 1. Reading MAX and seeding at MAX+2 avoids the
    ///       collision.</item>
    ///   </list>
    ///
    /// RACE HANDLING:
    ///   If two concurrent calls both reach this method for the same
    ///   Persian year (because both observed "no row exists"), the
    ///   unique index on Year will reject one of the INSERTs with
    ///   SqlException.Number == 2601. We catch that and retry the
    ///   UPDATE path — which now finds the winner's row and proceeds
    ///   as the steady-state case (with self-healing).
    ///
    ///   This race fires at most once per Persian year (literally the
    ///   first sale of year 1406, 1407, etc.). The retry succeeds on
    ///   the first re-attempt because the winner's INSERT has committed
    ///   by the time the loser's INSERT fails.
    /// </summary>
    private static async Task<int> InsertFirstRowOrRetryUpdateAsync(
        SqlConnection connection,
        int persianYear,
        CancellationToken cancellationToken)
    {
        // ------------------------------------------------------------------
        // Read the current MAX(SaleNumber_Sequence) for this Persian year
        // from the Sales table. Returns 0 if no sales exist yet for this
        // year (genuinely new year). Returns the highest allocated
        // sequence if sales exist but the counter row is missing (the
        // "self-heal on first allocation" case).
        // ------------------------------------------------------------------
        var maxExistingSequence = await ReadMaxSaleSequenceAsync(
            connection, persianYear, cancellationToken);

        var allocatedSequence = maxExistingSequence + 1;
        var nextSequenceForFutureCallers = maxExistingSequence + 2;

        const string insertSql = @"
INSERT INTO SaleSequenceCounters (Id, Year, NextSequence)
VALUES (@Id, @Year, @NextSequence);";

        try
        {
            await using var insertCmd = new SqlCommand(insertSql, connection);
            insertCmd.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            insertCmd.Parameters.Add("@Year", SqlDbType.Int).Value = persianYear;
            insertCmd.Parameters.Add("@NextSequence", SqlDbType.Int).Value = nextSequenceForFutureCallers;
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);

            // INSERT succeeded — we own the row, and we've allocated
            // `allocatedSequence` (= MAX + 1, or 1 if MAX was 0).
            return allocatedSequence;
        }
        catch (SqlException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Lost the first-sale-of-year race — another caller just
            // inserted the row. Retry the UPDATE path; it will now find
            // the winner's row and allocate the next sequence (with
            // self-healing, in case the winner's INSERT seeded a value
            // that's already behind MAX(Sales)).
            var retryResult = await TryAllocateFromExistingRowAsync(
                connection, persianYear, cancellationToken);

            if (retryResult is null)
            {
                // Extremely unlikely: the row vanished between the
                // failed INSERT and the retry UPDATE. This would only
                // happen if a DBA manually deleted the counter row in
                // the millisecond between our two statements. Surface
                // it as a transient error so the caller's retry loop
                // (UnitOfWork.ExecuteWithRetryAsync) can re-run the
                // whole handler.
                throw new InvalidOperationException(
                    $"SaleNumberGenerator: counter row for Persian year {persianYear} " +
                    "disappeared between the failed INSERT and the retry UPDATE. " +
                    "This is a transient error — the caller's retry will resolve it.");
            }

            return retryResult.Value;
        }
    }

    /// <summary>
    /// Reads <c>MAX(SaleNumber_Sequence)</c> from the <c>Sales</c> table
    /// for the given Persian year. Returns <c>0</c> if no sales exist for
    /// that year yet (the <c>ISNULL</c> coerces SQL <c>NULL</c> to 0).
    ///
    /// Used by <see cref="InsertFirstRowOrRetryUpdateAsync"/> to seed the
    /// counter at the correct value when no counter row exists yet —
    /// handles both the "genuinely new year" case (no sales) and the
    /// "counter row was deleted/missing" case (sales exist).
    ///
    /// ISOLATION: uses READ COMMITTED (the SQL Server default). We don't
    /// need a higher isolation here because:
    ///   - If we read a slightly stale MAX (a concurrent INSERT just
    ///     committed a higher sequence but we don't see it yet), the
    ///     INSERT into SaleSequenceCounters still succeeds (we're
    ///     creating a new row, not conflicting with anything). The
    ///     allocated sequence might be 1 lower than it should be —
    ///     but the unique index on Sales catches it, and the UnitOfWork
    ///     retry calls us again, at which point MAX has caught up.
    ///   - The self-healing UPDATE in <see cref="TryAllocateFromExistingRowAsync"/>
    ///     is the backstop: even if the INSERT seeds a counter that's
    ///     behind MAX, the next allocation via the UPDATE path will
    ///     detect and heal the drift.
    /// </summary>
    private static async Task<int> ReadMaxSaleSequenceAsync(
        SqlConnection connection,
        int persianYear,
        CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT ISNULL(MAX(s.SaleNumber_Sequence), 0)
FROM Sales s
WHERE s.SaleNumber_Year = @Year;";

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add("@Year", SqlDbType.Int).Value = persianYear;

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is int max ? max : 0;
    }

    /// <summary>
    /// Returns true if the given <see cref="SqlException"/> represents a
    /// SQL Server unique-constraint violation. We use this to detect the
    /// "first sale of the year" race where two concurrent INSERTs both
    /// target the same Persian year — the loser's INSERT fails with
    /// error 2601, which we catch and retry as an UPDATE.
    ///
    /// SQL Server error numbers we recognize:
    ///   2601 — Cannot insert duplicate key row in object '...' with
    ///          unique index '...'. (Fired by the IX_SaleSequenceCounters_Year
    ///          unique index we declared in the migration.)
    ///   2627 — Violation of PRIMARY KEY constraint '...'. (Defensive —
    ///          would fire if someone ever adds a row with a duplicate Id.
    ///          With Guid.NewGuid() this is astronomically unlikely, but
    ///          catching it costs nothing and the retry path is the same.)
    /// </summary>
    private static bool IsUniqueConstraintViolation(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (error.Number == 2601 || error.Number == 2627)
            {
                return true;
            }
        }
        return false;
    }
}
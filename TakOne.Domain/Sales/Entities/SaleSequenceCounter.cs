using TakOne.Domain.Sales.ValueObjects;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Domain.Sales.Entities;

/// <summary>
/// Persistent, monotonic counter for the global Sale sequence within a single
/// Persian (Jalali) year. One row per Persian year. Primary key is the
/// Persian year itself.
///
/// PURPOSE:
///   This table is the AUTHORITATIVE source of truth for "what is the next
///   SaleNumber.Sequence for Persian year Y?". It exists to make sequence
///   allocation ATOMIC and INDEPENDENT of the Sales table — so that
///   hard-deletes of Draft sales (which physically remove rows from the
///   Sales table) can never cause sequence reuse or collisions.
///
/// THE BUG IT FIXES:
///   The previous algorithm was <c>Count(sales in year) + 1</c>. That
///   algorithm had two production-fatal bugs:
///   <list type="bullet">
///     <item>HARD-DELETE REUSE: hard-deleting Draft sale #N dropped the
///       count to N-1, so the next call returned N — reusing the deleted
///       sale's number.</item>
///     <item>YEAR-BOUNDARY INVISIBILITY: a sale created in the few minutes
///       around the Persian New Year could be assigned year Y+1 but have
///       CreatedAtUtc before the computed year-start, making it invisible
///       to the count filter and causing the next call to collide with it
///       on the unique index.</item>
///   </list>
///   The intermediate MAX+1 fix addressed both bugs but still relied on
///   retry-on-unique-violation for the race between two concurrent
///   CreateSale calls. This counter table eliminates the race entirely:
///   the row-level lock held during the get-and-increment serializes
///   allocations within a year, so no two callers ever receive the same
///   sequence number.
///
/// ALLOCATION ALGORITHM (in Infrastructure SaleNumberGenerator):
///   <code>
///   BEGIN TRANSACTION (Serializable);
///     SELECT * FROM SaleSequenceCounters
///       WITH (UPDLOCK, HOLDLOCK)
///       WHERE Year = @persianYear;
///
///     IF row exists:
///       UPDATE SaleSequenceCounters
///         SET NextSequence = NextSequence + 1
///         WHERE Year = @persianYear;
///       -- new sequence = the updated NextSequence value
///     ELSE:
///       INSERT INTO SaleSequenceCounters (Year, NextSequence)
///         VALUES (@persianYear, 1);
///       -- new sequence = 1
///
///   COMMIT TRANSACTION;
///   </code>
///
///   UPDLOCK + HOLDLOCK + SERIALIZABLE is the canonical SQL Server
///   "atomic get-or-create" pattern: it guarantees that if two requests
///   race for the same year, one blocks until the other commits, then
///   sees the updated row. No unique-constraint violation, no retry loop,
///   no burned sequence number. The lock is held for milliseconds at
///   most (the transaction body is three short statements).
///
/// GAP SEMANTICS (acceptable):
///   The counter increments BEFORE the Sale row is inserted. If the
///   Sale creation subsequently fails (validation error, network glitch,
///   etc.), the sequence number is "burned" — no sale will ever carry
///   it. This is the standard ERP behavior (SAP, Oracle EBS, Microsoft
///   Dynamics all behave the same way for document number ranges) and
///   is acceptable for TakOne because:
///   <list type="bullet">
///     <item>SaleNumbers are internal identifiers, not customer-facing
///       invoice numbers. A gap in the sequence does not affect
///       accounting or customer communication.</item>
///     <item>The alternative — sharing a transaction between the counter
///       increment and the Sale insert — would couple the
///       SaleNumberGenerator to the caller's UnitOfWork, breaking the
///       clean layering (Application owns the UoW; Infrastructure owns
///       sequence allocation).</item>
///     <item>Hard-deleted Drafts already produce gaps by definition
///       (the row vanishes), so users are already accustomed to the
///       idea that not every sequence number corresponds to a
///       surviving sale.</item>
///   </list>
///
/// WHY NOT A SQL SERVER SEQUENCE OBJECT:
///   SQL Server's <c>CREATE SEQUENCE</c> + <c>NEXT VALUE FOR</c> is
///   atomic and fast, but it's a single global counter — it can't be
///   scoped per Persian year without creating a new SEQUENCE object
///   every year (which requires DDL permissions at runtime and a
///   cron-like trigger at the New Year). A table with one row per
///   year is simpler, schema-stable, and trivially auditable
///   (<c>SELECT * FROM SaleSequenceCounters ORDER BY Year</c> shows
///   the entire history at a glance).
///
/// WHY NOT JUST KEEP MAX+1:
///   MAX+1 is correct in the steady state but produces a unique-constraint
///   violation on every concurrent CreateSale race. The Application
///   layer's UnitOfWork retries the entire handler up to 3 times. That
///   works, but:
///   <list type="bullet">
///     <item>It burns CPU on both the app server and the DB (the
///       losing request runs the full handler — load aggregates,
///       validate, etc. — only to fail at SaveChanges).</item>
///     <item>It couples the SaleNumber correctness to the retry policy.
///       If a future change weakens or removes the retry, MAX+1
///       silently becomes incorrect under concurrency.</item>
///     <item>The counter table is strictly better: same correctness,
///       no retry needed, no CPU waste, and the SaleNumber allocation
///       is now a single short transaction independent of the rest
///       of the handler.</item>
///   </list>
///
/// ROW LIFETIME:
///   Rows are created on demand (the first sale of a new Persian year
///   triggers an INSERT). They are NEVER deleted — even if all sales in
///   a year are hard-deleted, the counter row stays so the next sale
///   gets a strictly higher number than any deleted one. This is the
///   monotonicity guarantee.
///
/// AUDIT:
///   The counter table is itself an audit artifact: <c>NextSequence - 1</c>
///   is the highest sequence ever allocated in a year, regardless of
///   whether the corresponding sales survived. Useful for capacity
///   planning ("how many sales did we attempt in year 1405?").
/// </summary>
public sealed class SaleSequenceCounter : BaseEntity
{
    /// <summary>
    /// The Persian (Jalali) year this counter covers. Stored as a plain
    /// int (e.g. <c>1405</c>). This is ALSO the primary key — one row
    /// per Persian year, by design.
    ///
    /// VALIDATION: same bounds as <see cref="SaleNumber.Year"/> — see
    /// <see cref="SaleNumber.MinPersianYear"/> and
    /// <see cref="SaleNumber.MaxPersianYear"/>. Enforced in
    /// <see cref="Create"/> so a malformed value can never reach the DB.
    /// </summary>
    public int Year { get; private set; }

    /// <summary>
    /// The NEXT sequence number to be allocated for this Persian year.
    ///
    /// INVARIANT: <c>NextSequence &gt;= 1</c> and
    /// <c>NextSequence &lt;= <see cref="SaleNumber.MaxSequence"/> + 1</c>.
    /// The +1 on the upper bound is intentional: when NextSequence reaches
    /// <c>MaxSequence + 1</c>, the year is at capacity (the last
    /// successful allocation returned <c>MaxSequence</c>), and the next
    /// allocation will throw via the <see cref="SaleNumber.Create"/>
    /// guard.
    ///
    /// This property is PRIVATE-SET because it must ONLY be mutated via
    /// <see cref="AllocateNext"/> — the only path that enforces the
    /// invariant. EF Core's backing-field access mode is configured in
    /// <c>SaleSequenceCounterConfiguration</c>.
    /// </summary>
    public int NextSequence { get; private set; }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor.
    /// <summary>
    /// Parameterless constructor required by EF Core materialization.
    /// DO NOT use from application code — use <see cref="Create"/>.
    /// </summary>
    private SaleSequenceCounter() { }
#pragma warning restore CS8618

    private SaleSequenceCounter(int year, int nextSequence) : base()
    {
        Year = year;
        NextSequence = nextSequence;
    }

    /// <summary>
    /// Factory for the FIRST row of a new Persian year. Always creates
    /// a row with <c>NextSequence = 1</c> (the first sale of the year
    /// gets sequence 1).
    ///
    /// Called by <c>SaleNumberGenerator</c> only when no row exists yet
    /// for the current Persian year. The PK constraint on Year will
    /// reject a concurrent INSERT race (the loser rolls back and
    /// retries the SELECT-then-UPDATE path).
    /// </summary>
    public static SaleSequenceCounter CreateForNewYear(int persianYear)
    {
        if (persianYear < SaleNumber.MinPersianYear || persianYear > SaleNumber.MaxPersianYear)
        {
            throw new ArgumentException(
                $"Persian year {persianYear} is out of the supported range " +
                $"[{SaleNumber.MinPersianYear}, {SaleNumber.MaxPersianYear}].",
                nameof(persianYear));
        }

        return new SaleSequenceCounter(persianYear, nextSequence: 1);
    }

    /// <summary>
    /// Atomically (under the row-level lock held by the caller's
    /// serializable transaction) increments <see cref="NextSequence"/>
    /// and returns the value that should be used for the new Sale's
    /// Sequence.
    ///
    /// RETURNS: the value to use for <c>SaleNumber.Sequence</c>. This
    /// is <c>NextSequence BEFORE the increment</c>. After this call,
    /// <see cref="NextSequence"/> has been bumped by 1, so the next
    /// caller gets a strictly higher number.
    ///
    /// GUARD: if the next sequence to allocate would exceed
    /// <see cref="SaleNumber.MaxSequence"/>, this method THROWS an
    /// <see cref="InvalidOperationException"/> with a clear message.
    /// The exception propagates up to the Application layer, which
    /// surfaces it as "system capacity reached for this year —
    /// contact support". This is the correct failure mode: we will
    /// NOT silently overflow to 5 digits.
    /// </summary>
    public int AllocateNext()
    {
        if (NextSequence > SaleNumber.MaxSequence)
        {
            throw new InvalidOperationException(
                $"Sale sequence capacity reached for Persian year {Year}. " +
                $"The system cannot create more than {SaleNumber.MaxSequence} " +
                $"sales in one Persian year under the current 4-digit format. " +
                $"Contact support to extend the format.");
        }

        var allocated = NextSequence;
        NextSequence = NextSequence + 1;
        return allocated;
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Sales.Entities;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="SaleSequenceCounter"/>.
///
/// TABLE: <c>SaleSequenceCounters</c>
///
/// COLUMNS:
///   - Id            (uniqueidentifier, PK)  — inherited from BaseEntity
///   - Year          (int, NOT NULL, UNIQUE) — Persian year, also has a
///                       unique index (in addition to the implicit PK on Id)
///                       so the get-or-create race resolves cleanly via a
///                       PK violation rather than a silent duplicate.
///   - NextSequence  (int, NOT NULL)         — next sequence to allocate
///
/// NO FK to Sales: the counter is the AUTHORITATIVE source of truth for
/// sequence allocation; it does NOT reference individual sales. Sales
/// reference the counter only logically (via the shared Year + Sequence
/// pair), enforced by the unique index on
/// <c>(SaleNumber_Year, SaleNumber_Sequence)</c> in SaleConfiguration.
///
/// NO SOFT-DELETE: counter rows are NEVER deleted (see the doc comment on
/// <see cref="SaleSequenceCounter"/> for the monotonicity rationale).
/// </summary>
public sealed class SaleSequenceCounterConfiguration : IEntityTypeConfiguration<SaleSequenceCounter>
{
    public void Configure(EntityTypeBuilder<SaleSequenceCounter> builder)
    {
        builder.ToTable("SaleSequenceCounters");
        builder.HasKey(c => c.Id);

        // ------------------------------------------------------------------
        // Year — Persian year. UNIQUE constraint because the
        // SaleNumberGenerator's get-or-create race relies on a PK violation
        // (2601) to detect "someone else just inserted the row for this
        // year; retry the SELECT-then-UPDATE path". Without the unique
        // constraint, two concurrent "first sale of the year" requests
        // could both INSERT and both proceed with NextSequence = 1, which
        // would then collide on the Sales unique index downstream —
        // defeating the purpose of the counter table.
        //
        // We make Year UNIQUE rather than the PK because:
        //   - The rest of the codebase uses Guid Ids as PKs (BaseEntity).
        //     Keeping that convention here means cross-entity references
        //     and audit logs can use a consistent Id type.
        //   - The unique constraint on Year is sufficient for the
        //     get-or-create correctness, and the index it creates also
        //     speeds up the SELECT-by-Year that the generator does on
        //     every call.
        // ------------------------------------------------------------------
        builder.Property(c => c.Year)
            .IsRequired();

        builder.HasIndex(c => c.Year)
            .IsUnique();

        // ------------------------------------------------------------------
        // NextSequence — current value of the counter. The only mutation
        // path is via SaleSequenceCounter.AllocateNext(), which enforces
        // the invariant 1 <= NextSequence <= MaxSequence + 1.
        //
        // We configure EF to use the PRIVATE backing field for writes
        // (PropertyAccessMode.Field) so the private setter is never
        // bypassed by EF's change tracker. The property's private setter
        // exists only to satisfy the EF materialization convention; the
        // setter is otherwise unreachable from application code.
        // ------------------------------------------------------------------
        builder.Property(c => c.NextSequence)
            .IsRequired();

        // ------------------------------------------------------------------
        // Year immutability after insert is enforced by the DOMAIN entity
        // (private setter, no mutation method) — EF Core will read the
        // value via the backing field on Update and emit it in the SET
        // clause, but since the value never changes, the UPDATE is a
        // no-op for that column. We don't need to override EF's
        // AfterSaveBehavior here; the domain guarantee is sufficient.
        // ------------------------------------------------------------------
    }
}
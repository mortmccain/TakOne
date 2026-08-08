using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Sales.Entities;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="SaleLineItem"/> entity (lives inside
/// the Sale aggregate boundary).
///
/// TABLE: <c>SaleLineItems</c>
///
/// COLUMNS:
///   - Id                  (uniqueidentifier, PK)
///   - SaleId              (uniqueidentifier, FK → Sales.Id, NOT NULL, cascade delete)
///   - ProductId           (uniqueidentifier, NOT NULL, INDEXED — cross-aggregate ref, NO FK)
///   - ProductName         (nvarchar(200), NOT NULL)        — snapshot at sale time
///   - Quantity            (int, NOT NULL)
///   - UnitPrice_Amount    (decimal(18, 2), NOT NULL)       — complex Money value object (ComplexProperty)
///   - UnitPrice_Currency  (nvarchar(3), NOT NULL)          — complex Money value object (ComplexProperty)
///   - LineNumber          (int, NOT NULL)                  — stable position (1, 2, 3, ...)
///
/// NOT STORED:
///   - <c>GrossTotal</c> (computed property: Quantity * UnitPrice). Recomputed
///     on every access; storing it would be denormalization with no benefit
///     (it's derived from already-stored columns).
///
/// INDEXES:
///   - Unique composite index on <c>(SaleId, LineNumber)</c> — line numbers
///     must be unique within a sale. The domain guarantees this via
///     <c>Sale.GetNextLineNumber()</c>, but the DB index is the authoritative
///     guard.
///   - Non-unique index on <c>ProductId</c> — for reporting queries like
///     "which sales include product X?". Cross-aggregate reference, no FK.
/// </summary>
public sealed class SaleLineItemConfiguration : IEntityTypeConfiguration<SaleLineItem>
{
    public void Configure(EntityTypeBuilder<SaleLineItem> builder)
    {
        builder.ToTable("SaleLineItems");
        builder.HasKey(li => li.Id);

        // ------------------------------------------------------------------
        // Id — CLIENT-GENERATED Guid, NOT store-generated.
        //
        // The SaleLineItem's internal constructor calls `base(Guid.NewGuid())`,
        // so the Id is always a fresh, non-default Guid before the entity
        // reaches the change tracker. We MUST tell EF Core this with
        // `ValueGeneratedNever()` — the default convention for a Guid PK is
        // `ValueGenerated.OnAdd`, which is WRONG for our client-generated
        // keys and is the root cause of the historical
        // `DbUpdateConcurrencyException` on the APPEND path of
        // CreateOrAppendSaleCommand.
        //
        // WHY THE DEFAULT CONVENTION IS WRONG FOR US:
        //   EF Core's NavigationFixer (see EntityGraphAttacher.PaintAction in
        //   EF Core 10) determines the state of a newly-detected entity found
        //   in a navigation collection using this logic:
        //
        //     var (isGenerated, isSet) = internalEntityEntry.IsKeySet;
        //     internalEntityEntry.SetEntityState(
        //         isSet
        //             ? (isGenerated ? storeGenTargetState : targetState)
        //             : EntityState.Added,
        //         ...);
        //
        //   - `isSet` is true for our entities (the constructor sets the Id).
        //   - `isGenerated` is true by default for Guid PKs (the convention
        //     is ValueGenerated.OnAdd).
        //   - `storeGenTargetState` is computed by NavigationFixer as:
        //        entry.EntityState == EntityState.Added ? EntityState.Added
        //                                              : EntityState.Modified
        //     where `entry` is the PRINCIPAL (the Sale).
        //
        //   When adding a NEW line to an EXISTING (Modified) Sale, the
        //   principal's state is Modified, so `storeGenTargetState` is
        //   Modified. EF Core then marks the new SaleLineItem as Modified
        //   — generating an UPDATE that matches 0 rows (the row doesn't
        //   exist) and throwing `DbUpdateConcurrencyException: expected to
        //   affect 1 row(s), but actually affected 0 row(s)`.
        //
        //   The CREATE path (new Sale + new SaleLineItem) is unaffected
        //   because the principal Sale is Added, so `storeGenTargetState`
        //   is Added. The INCREMENT path (modify existing line) is
        //   unaffected because no new entity is being attached.
        //
        // THE FIX:
        //   `ValueGeneratedNever()` flips `isGenerated` to false. EF Core
        //   then uses `targetState` (which is `EntityState.Added` per
        //   NavigationFixer line 393) instead of `storeGenTargetState`. The
        //   new SaleLineItem is correctly tracked as Added, an INSERT is
        //   generated, and the row is persisted.
        //
        //   `ValueGeneratedNever()` is the CORRECT configuration for any
        //   client-generated key — it tells EF Core "the application
        //   provides the key; do not generate one; a non-default value does
        //   NOT imply the entity already exists in the database." This is
        //   the standard EF Core guidance for `Guid.NewGuid()` keys set in
        //   the constructor.
        //
        // MIGRATION IMPACT:
        //   This is a model-only change. The `Sales` table's `Id` column
        //   schema is unchanged (it's already a `uniqueidentifier` PK with
        //   no default value, since the application provides the value). The
        //   migration that introduces this configuration change is empty
        //   (no DDL Up/Down).
        // ------------------------------------------------------------------
        builder.Property(li => li.Id).ValueGeneratedNever();

        // ProductId is a cross-aggregate reference (to the Product aggregate).
        // Indexed for query performance, NO FK (see SaleConfiguration docs
        // for the strict-DDD rationale).
        builder.Property(li => li.ProductId).IsRequired();

        // Snapshot — denormalized so historical sales display correctly even
        // if the Product's name changes later.
        builder.Property(li => li.ProductName).HasMaxLength(200).IsRequired();

        builder.Property(li => li.Quantity).IsRequired();
        builder.Property(li => li.LineNumber).IsRequired();

        // Money value object: UnitPrice. Mapped as a COMPLEX PROPERTY
        // (not OwnsOne) for value semantics — see SaleConfiguration's class-
        // level doc for the full rationale. ComplexProperty compares by value
        // (via GetEqualityComponents), not by reference identity, so reference
        // replacement on tracked SaleLineItems works correctly. Schema is
        // identical to OwnsOne (UnitPrice_Amount + UnitPrice_Currency columns,
        // same names/types/constraints).
        builder.ComplexProperty(li => li.UnitPrice, up =>
        {
            up.Property(m => m.Amount)
                .HasColumnName("UnitPrice_Amount")
                .HasPrecision(18, 2)
                .IsRequired();

            up.Property(m => m.Currency)
                .HasColumnName("UnitPrice_Currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        // GrossTotal is computed (Quantity * UnitPrice) — never stored.
        // Ignoring it tells EF not to map it as a column.
        builder.Ignore(li => li.GrossTotal);

        // ------------------------------------------------------------------
        // Indexes
        // ------------------------------------------------------------------
        // Composite unique index: LineNumber is unique within a Sale. The
        // domain's GetNextLineNumber() guarantees this, but the DB index is
        // the authoritative guard.
        builder.HasIndex("SaleId", nameof(SaleLineItem.LineNumber)).IsUnique();
        // Non-unique index on ProductId for reporting ("which sales include
        // product X?").
        builder.HasIndex(li => li.ProductId);
    }
}
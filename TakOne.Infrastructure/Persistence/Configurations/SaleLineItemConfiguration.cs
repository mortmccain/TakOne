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
///   - UnitPrice_Amount    (decimal(18, 2), NOT NULL)       — owned Money value object
///   - UnitPrice_Currency  (nvarchar(3), NOT NULL)          — owned Money value object
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

        // ProductId is a cross-aggregate reference (to the Product aggregate).
        // Indexed for query performance, NO FK (see SaleConfiguration docs
        // for the strict-DDD rationale).
        builder.Property(li => li.ProductId).IsRequired();

        // Snapshot — denormalized so historical sales display correctly even
        // if the Product's name changes later.
        builder.Property(li => li.ProductName).HasMaxLength(200).IsRequired();

        builder.Property(li => li.Quantity).IsRequired();
        builder.Property(li => li.LineNumber).IsRequired();

        // Owned value object: UnitPrice (Money). Same pattern as
        // Product.Price and Sale.Total.
        builder.OwnsOne(li => li.UnitPrice, up =>
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
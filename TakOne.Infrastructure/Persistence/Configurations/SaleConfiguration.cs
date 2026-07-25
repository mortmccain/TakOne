using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Sales.Entities;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="Sale"/> aggregate root.
///
/// TABLE: <c>Sales</c>
///
/// COLUMNS:
///   - Id                    (uniqueidentifier, PK)
///   - SaleNumber_Year       (int, NOT NULL)                — owned SaleNumber value object
///   - SaleNumber_Sequence   (int, NOT NULL)                — owned SaleNumber value object
///   - CustomerId            (uniqueidentifier, NOT NULL)   — cross-aggregate ref to User
///   - CustomerName          (nvarchar(200), NOT NULL)      — snapshot at sale time
///   - CreatedByUserId       (uniqueidentifier, NOT NULL)   — cross-aggregate ref to User
///   - CreatedByName         (nvarchar(200), NOT NULL)      — snapshot at sale time
///   - SubmittedByUserId     (uniqueidentifier, NOT NULL, default 0x00)
///   - ApprovedByUserId      (uniqueidentifier, NOT NULL, default 0x00)
///   - InvoicedByUserId      (uniqueidentifier, NOT NULL, default 0x00)
///   - CancelledByUserId     (uniqueidentifier, NOT NULL, default 0x00)
///   - Status                (int, NOT NULL)                — SaleStatus enum stored as int
///   - Total_Amount          (decimal(18, 2), NOT NULL)     — owned Money value object
///   - Total_Currency        (nvarchar(3), NOT NULL)        — owned Money value object
///   - CreatedAtUtc          (datetime2, NOT NULL)
///   - SubmittedAtUtc        (datetime2, NULL)
///   - ApprovedAtUtc         (datetime2, NULL)
///   - InvoicedAtUtc         (datetime2, NULL)
///   - CancelledAtUtc        (datetime2, NULL)
///   - CancellationReason    (nvarchar(max), NULL)
///
/// OWNED VALUE OBJECTS:
///   - <c>SaleNumber</c> (OwnsOne) — flattened into Sales table. The
///     <c>Value</c> string property is IGNORED (computed-on-access from
///     Year + Sequence, see SaleNumber.cs).
///   - <c>Total</c> (OwnsOne Money) — flattened into Sales table.
///
/// CRITICAL UNIQUE INDEX:
///   <c>(SaleNumber_Year, SaleNumber_Sequence)</c> is GLOBALLY UNIQUE. This
///   enforces the SaleNumber uniqueness contract: no two sales can share the
///   same SaleNumber. The unique index is also the concurrency guard for the
///   SaleNumberGenerator race condition (two concurrent CreateSale calls
///   that both compute the same sequence — the loser's SaveChangesAsync
///   fails with a unique-constraint violation, and the handler can retry).
///
/// CROSS-AGGREGATE REFERENCES — NO FKs:
///   CustomerId, CreatedByUserId, SubmittedByUserId, ApprovedByUserId,
///   InvoicedByUserId, CancelledByUserId all reference the User aggregate.
///   They're indexed for query performance but have NO DB-level FK
///   constraints (same strict-DDD reasoning as ProductConfiguration).
///
/// AUDIT ID COLUMNS — NON-NULLABLE Guid (default Guid.Empty):
///   The Domain uses Guid.Empty as "not yet set" for these audit fields
///   (e.g. ApprovedByUserId is Guid.Empty until Approve() is called). The
///   convention is that consumers read these only when the Sale is in (or
///   past) the corresponding state. We store them as NOT NULL with default
///   Guid.Empty so the column is always populated, and queries can use
///   `WHERE ApprovedByUserId != 0x00` to find sales that have been approved.
/// </summary>
public sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        builder.ToTable("Sales");
        builder.HasKey(s => s.Id);

        // ------------------------------------------------------------------
        // Owned value object: SaleNumber
        //
        // Flattened into Sales table. `Value` is computed-on-access from
        // Year + Sequence (see SaleNumber.cs), so we don't store it.
        // ------------------------------------------------------------------
        builder.OwnsOne(s => s.SaleNumber, sn =>
        {
            sn.Property(p => p.Year)
                .HasColumnName("SaleNumber_Year")
                .IsRequired();

            sn.Property(p => p.Sequence)
                .HasColumnName("SaleNumber_Sequence")
                .IsRequired();

            // IMPORTANT: ignore the `Value` property. It's computed from
            // Year + Sequence + Prefix on every access — storing it would
            // be redundant and would risk divergence.
            sn.Ignore(p => p.Value);
        });

        // The globally-unique SaleNumber index. This is the authoritative
        // enforcement of SaleNumber uniqueness AND the concurrency guard for
        // the SaleNumberGenerator race condition (two concurrent CreateSale
        // calls both computing sequence N — only one commits, the other gets
        // a unique-constraint violation on SaveChangesAsync).
        //
        // EF Core's OwnsOne creates the columns with the names we specified
        // above (SaleNumber_Year, SaleNumber_Sequence). The HasIndex call
        // uses the property paths to reference them.
        builder.HasIndex("SaleNumber_Year", "SaleNumber_Sequence").IsUnique();

        // ------------------------------------------------------------------
        // Cross-aggregate reference columns (User aggregate)
        //
        // All are Guid. NOT NULL because the domain always sets them (to
        // Guid.Empty when "not yet set", which is a valid non-null value).
        // No FK constraints — see class-level docs.
        // ------------------------------------------------------------------
        builder.Property(s => s.CustomerId).IsRequired();
        builder.Property(s => s.CreatedByUserId).IsRequired();
        builder.Property(s => s.ApprovedByUserId).IsRequired();
        builder.Property(s => s.InvoicedByUserId).IsRequired();
        builder.Property(s => s.CancelledByUserId).IsRequired();

        // Snapshot names — denormalized for audit/display so we don't have
        // to JOIN to Users just to show "customer name" on a historical sale.
        builder.Property(s => s.CustomerName).HasMaxLength(200).IsRequired();
        builder.Property(s => s.CreatedByName).HasMaxLength(200).IsRequired();

        // ------------------------------------------------------------------
        // Status — enum stored as int
        //
        // EF Core stores enums as int by default, but we use HasConversion<int>
        // to make it EXPLICIT. This protects against a future developer
        // changing SaleStatus to a long/byte enum and silently breaking the
        // column type.
        // ------------------------------------------------------------------
        builder.Property(s => s.Status)
            .HasConversion<int>()
            .IsRequired();

        // ------------------------------------------------------------------
        // Owned value object: Total (Money)
        //
        // Flattened into Sales table. Same pattern as Product.Price.
        // ------------------------------------------------------------------
        builder.OwnsOne(s => s.Total, total =>
        {
            total.Property(m => m.Amount)
                .HasColumnName("Total_Amount")
                .HasPrecision(18, 2)
                .IsRequired();

            total.Property(m => m.Currency)
                .HasColumnName("Total_Currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        // ------------------------------------------------------------------
        // Timestamps
        //
        // CreatedAtUtc is set in the Sale constructor and never changes.
        // The other four are nullable (set when the corresponding state
        // transition occurs).
        //
        // We use datetime2 (not datetime) for consistent precision across
        // SQL Server versions. datetime2(7) is the default and gives
        // 100-nanosecond precision, which is more than enough for audit logs.
        // ------------------------------------------------------------------
        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.SubmittedAtUtc);
        builder.Property(s => s.ApprovedAtUtc);
        builder.Property(s => s.InvoicedAtUtc);
        builder.Property(s => s.CancelledAtUtc);

        builder.Property(s => s.CancellationReason);

        // ------------------------------------------------------------------
        // LineItems collection — use the private backing field.
        //
        // Same pattern as Category.SubCategories: the public property is
        // IReadOnlyList<T> with no setter, so EF must populate the private
        // _lineItems field directly.
        // ------------------------------------------------------------------
        builder.HasMany(s => s.LineItems)
            .WithOne()
            .HasForeignKey("SaleId")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Sale.LineItems))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // ------------------------------------------------------------------
        // Indexes (query performance)
        // ------------------------------------------------------------------
        // CustomerId — for "show me this customer's sales" (customer dashboard).
        builder.HasIndex(s => s.CustomerId);

        // CreatedByUserId — for "show me the sales I started" (employee
        // dashboard). This is the column that SaleByCreatorSpecification
        // filters on.
        builder.HasIndex(s => s.CreatedByUserId);

        // Status — for "show me all pending sales" (employee approval queue).
        builder.HasIndex(s => s.Status);

        // CreatedAtUtc — for date-range queries and reports.
        builder.HasIndex(s => s.CreatedAtUtc);
    }
}
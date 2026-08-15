using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.ValueObjects;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="Sale"/> aggregate root.
///
/// TABLE: <c>Sales</c>
///
/// COLUMNS:
///   - Id                    (uniqueidentifier, PK)
///   - SaleNumber_Year       (int, NULL)                    — owned SaleNumber value object (NULL for drafts)
///   - SaleNumber_Sequence   (int, NULL)                    — owned SaleNumber value object (NULL for drafts)
///   - CustomerId            (uniqueidentifier, NOT NULL)   — cross-aggregate ref to User
///   - CustomerName          (nvarchar(200), NOT NULL)      — snapshot at sale time
///   - CreatedByUserId       (uniqueidentifier, NOT NULL)   — cross-aggregate ref to User
///   - CreatedByName         (nvarchar(200), NOT NULL)      — snapshot at sale time
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
///   - <c>SaleNumber</c> (OwnsOne, OPTIONAL navigation) — flattened into Sales table.
///     The navigation is NULL while the Sale is in Draft status (the permanent
///     sale number is allocated only on Submit(), per the B2 deferred-allocation
///     design). When the navigation is NULL, EF Core writes NULL to both
///     SaleNumber_Year and SaleNumber_Sequence columns. The <c>Value</c> string
///     property is IGNORED (computed-on-access from Year + Sequence, see SaleNumber.cs).
///     Kept as OwnsOne (not ComplexProperty) because it requires a globally-unique
///     (Year, Sequence) index, and ComplexProperty does not support indexes.
///   - <c>Total</c> (ComplexProperty Money) — flattened into Sales table.
///     Mapped as ComplexProperty (not OwnsOne) for value semantics: EF
///     Core compares Money instances by value, not by reference identity,
///     so Sale.RecalculateTotal's reference-replacement pattern works
///     correctly. See the inline comment on the Total mapping below for
///     the full rationale.
///
/// CRITICAL UNIQUE INDEX (FILTERED):
///   <c>(SaleNumber_Year, SaleNumber_Sequence)</c> is UNIQUE, but only on rows
///   where both columns are NON-NULL — i.e. only on submitted sales. This is
///   a FILTERED unique index (SQL Server syntax: <c>WHERE [SaleNumber_Year] IS
///   NOT NULL AND [SaleNumber_Sequence] IS NOT NULL</c>). Without the filter,
///   multiple Draft rows (all with NULL sale numbers) would collide and violate
///   the unique constraint. The filter enforces the SaleNumber uniqueness
///   contract for submitted sales while leaving drafts free to have NULL numbers.
///   The unique index is also the concurrency guard for the SaleNumberGenerator
///   race condition (two concurrent SubmitSale calls that both compute the same
///   sequence — the loser's SaveChangesAsync fails with a unique-constraint
///   violation, and the handler can retry).
///
/// CROSS-AGGREGATE REFERENCES — NO FKs:
///   CustomerId, CreatedByUserId, ApprovedByUserId, InvoicedByUserId,
///   CancelledByUserId all reference the User aggregate.
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
        // Owned value object: SaleNumber (OPTIONAL navigation)
        //
        // The Sale.SaleNumber navigation is NULLABLE (SaleNumber? on the entity).
        // It is NULL while the Sale is in Draft status — the permanent sale
        // number is allocated only when the customer submits the cart (see
        // Sale.Submit() and SubmitSaleCommandHandler). This is the B2
        // deferred-allocation design: drafts don't burn permanent sequence
        // numbers, keeping the audit trail of POSTED sales gap-free.
        //
        // NULLABILITY PLUMBING:
        //   - Sale.SaleNumber is declared as `SaleNumber?` in the domain.
        //   - We mark the NAVIGATION as optional via
        //     `builder.Navigation(s => s.SaleNumber).IsRequired(false)`
        //     (see below the OwnsOne block). That tells EF Core the owned
        //     entity can be absent, which automatically makes ALL its columns
        //     nullable in the database.
        //   - We must NOT call `.IsRequired(false)` on the individual `Year`
        //     and `Sequence` properties — they are `int` (non-nullable value
        //     types), and EF Core rejects marking a non-nullable CLR type as
        //     nullable/optional at the property level. The column nullability
        //     is driven entirely by the optional navigation.
        //   - When the navigation is NULL, EF Core writes NULL to BOTH columns.
        //     When the navigation is non-NULL, EF writes the Year and Sequence
        //     values (both are guaranteed non-null by the SaleNumber value
        //     object's constructor).
        //
        // `Value` is computed-on-access from Year + Sequence + Prefix (see
        // SaleNumber.cs) — we ignore it so EF doesn't try to map (and write
        // to) the read-only expression-bodied property. `Prefix` is
        // `public const string` on SaleNumber, so it's a static member; EF
        // Core's conventions already skip static/const members and they will
        // NOT become columns — no explicit Ignore needed.
        // (Attempting `sn.Ignore(p => p.Prefix)` is a compile error: const
        // members cannot be accessed via an instance reference.)
        // ------------------------------------------------------------------
        builder.OwnsOne(s => s.SaleNumber, sn =>
        {
            // Column names are explicit so the filtered unique index below
            // can reference them by name in the HasFilter SQL. The columns
            // are nullable because the navigation is optional (see the
            // Navigation(...).IsRequired(false) call below).
            sn.Property(p => p.Year)
                .HasColumnName("SaleNumber_Year");

            sn.Property(p => p.Sequence)
                .HasColumnName("SaleNumber_Sequence");

            // IMPORTANT: ignore the `Value` computed property. Storing it
            // would be redundant (it's derivable from Year + Sequence) and
            // would risk divergence if Year/Sequence ever changed without
            // re-deriving Value.
            sn.Ignore(p => p.Value);

            // The globally-unique SaleNumber index — FILTERED so it only
            // applies to rows where both columns are NON-NULL (i.e. submitted
            // sales). Without the filter, multiple Draft rows (all with NULL
            // sale numbers) would collide and violate the unique constraint.
            //
            // This is the authoritative enforcement of SaleNumber uniqueness
            // AND the concurrency guard for the SaleNumberGenerator race
            // condition (two concurrent SubmitSale calls both computing
            // sequence N — only one commits, the other gets a unique-
            // constraint violation on SaveChangesAsync).
            //
            // WHY THIS LIVES INSIDE OwnsOne (not outside on builder.HasIndex):
            //   EF Core's HasIndex lambda only accepts DIRECT property access
            //   on the entity being configured — it does NOT support navigating
            //   THROUGH an owned navigation. So `builder.HasIndex(s => new {
            //   s.SaleNumber.Year, s.SaleNumber.Sequence })` throws:
            //     "The expression 's => new <>f__AnonymousType0`2(Year =
            //      s.SaleNumber.Year, Sequence = s.SaleNumber.Sequence)'
            //      is not a valid member access expression."
            //   And `builder.HasIndex("SaleNumber_Year", "SaleNumber_Sequence")`
            //   ALSO fails (the previous attempt) because those names belong
            //   to the owned entity type, not to Sale — EF treats them as
            //   shadow properties on Sale and can't infer a type.
            //
            //   The CORRECT place to declare an index on owned-entity
            //   properties is INSIDE the OwnsOne lambda, where `sn` is the
            //   owned entity's configuration builder and the properties ARE
            //   direct members. EF will then flatten the index onto the
            //   parent (Sales) table using the column names we declared above
            //   (SaleNumber_Year, SaleNumber_Sequence).
            //
            // FILTER SYNTAX:
            //   We use SQL Server's bracketed-identifier syntax
            //   ([ColumnName] IS NOT NULL). EF Core passes the filter string
            //   verbatim to the provider, so it must match the target DB's
            //   SQL dialect. SQLite also accepts this syntax (treats [] as
            //   optional quoting), so the same filter works for in-memory
            //   SQLite tests if we add them later.
            sn.HasIndex(p => new { p.Year, p.Sequence })
                .IsUnique()
                .HasFilter("[SaleNumber_Year] IS NOT NULL AND [SaleNumber_Sequence] IS NOT NULL");
        });

        // Explicitly mark the owned navigation as optional. This is technically
        // redundant with the nullable C# property (EF Core 6+ infers it), but
        // making it explicit guards against a future developer changing the
        // property back to non-nullable without realizing the DB schema still
        // needs to support NULLs for legacy draft rows.
        builder.Navigation(s => s.SaleNumber)
            .IsRequired(false);

        // ------------------------------------------------------------------
        // Cross-aggregate reference columns (User aggregate)
        //
        // All are Guid. NOT NULL because the domain always sets them (to
        // Guid.Empty when "not yet set", which is a valid non-null value).
        // No FK constraints — see class-level docs.
        // ------------------------------------------------------------------
        builder.Property(s => s.CustomerId).IsRequired();
        builder.Property(s => s.CreatedByUserId).IsRequired();
        // NOTE: no SubmittedByUserId column — the submitter is always the
        // sale's CustomerId (enforced by SubmitSaleCommandHandler). Storing
        // it again would be redundant denormalization.
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
        // Mapped as a COMPLEX PROPERTY (not OwnsOne). ComplexProperty was
        // introduced in EF Core 9 specifically for value objects. Unlike
        // OwnsOne, it has VALUE SEMANTICS: EF Core compares complex type
        // instances by value (via GetEqualityComponents on BaseValueObject),
        // not by reference identity.
        //
        // WHY THIS MATTERS:
        //   Sale.RecalculateTotal() does `Total = _lineItems.Aggregate(
        //   Money.Zero(currency), (sum, item) => sum + item.GrossTotal)` —
        //   it REPLACES the Total reference with a brand-new Money instance
        //   (the `+` operator always returns a new instance). With OwnsOne,
        //   this reference replacement confused the change tracker (it had
        //   two Money instances for the same navigation: the old tracked
        //   one and the new one), and SaveChanges generated an UPDATE whose
        //   WHERE clause matched 0 rows:
        //
        //       DbUpdateConcurrencyException: expected to affect 1 row(s),
        //       but actually affected 0 row(s)
        //
        //   This was the root cause of the persistent
        //   CreateOrAppendSaleCommand failures on the APPEND path (adding
        //   to an existing draft cart). With ComplexProperty, replacing the
        //   reference is the idiomatic mutation pattern — EF detects the
        //   value change and generates a clean UPDATE.
        //
        // SCHEMA: identical to OwnsOne. The columns Total_Amount and
        // Total_Currency are flattened into the Sales table, with the same
        // names, types, and constraints. The migration that switches from
        // OwnsOne to ComplexProperty is therefore a model-only change —
        // no DDL is generated (the table is unchanged).
        //
        // WHY NOT ComplexProperty for SaleNumber (above):
        //   ComplexProperty does NOT support indexes on nested properties.
        //   SaleNumber requires a globally-unique (Year, Sequence) index,
        //   so it MUST stay as OwnsOne. SaleNumber is also never replaced
        //   after construction (it's set once in Sale.Create and never
        //   mutated), so the reference-replacement bug doesn't apply to it.
        // ------------------------------------------------------------------
        builder.ComplexProperty(s => s.Total, total =>
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
        // LineItems collection — SaleLineItem has NO public SaleId property
        // (the aggregate boundary is kept clean: children don't reference
        // their parent). We model the FK as a SHADOW PROPERTY named "SaleId"
        // so the DB schema and EF model still see the column, but the domain
        // class doesn't. EF writes/reads it transparently during SaveChanges.
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
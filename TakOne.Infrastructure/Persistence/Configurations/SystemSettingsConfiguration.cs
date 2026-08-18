using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Common.Entities;
using TakOne.Domain.Common.Enums;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="SystemSettings"/> singleton entity.
///
/// TABLE: <c>SystemSettings</c>
///
/// SINGLETON PATTERN:
///   This table contains EXACTLY ONE row, identified by the constant
///   <see cref="SystemSettings.SingletonId"/> (<see cref="Guid.Empty"/>).
///   The check constraint below enforces this at the database level —
///   only a row whose Id = '00000000-0000-0000-0000-000000000000' may
///   exist, and the unique index on Id prevents a second singleton row
///   from being inserted.
///
/// COLUMNS:
///   - Id         (uniqueidentifier, PK — always Guid.Empty)
///   - LimitMode  (int, NOT NULL — EF Core maps the enum to its
///                 underlying int automatically)
///   - UpdatedAt  (datetime2, NOT NULL)
///
/// CACHING (handled in <c>SystemSettingsService</c>, not here):
///   Reads are cached in-process via <c>IMemoryCache</c>. The cache
///   entry is invalidated by <c>SetSystemLimitModeCommandHandler</c>
///   after every successful update. In steady state, zero DB hits
///   for the settings check — the cache is the hot path.
/// </summary>
public sealed class SystemSettingsConfiguration : IEntityTypeConfiguration<SystemSettings>
{
    public void Configure(EntityTypeBuilder<SystemSettings> builder)
    {
        // ------------------------------------------------------------------
        // Table & primary key
        // ------------------------------------------------------------------
        builder.ToTable("SystemSettings", t =>
        {
            // ------------------------------------------------------------------
            // Check constraint: enforce that the singleton row's Id is always
            // Guid.Empty. This is a defense-in-depth against a buggy future
            // codepath that tries to insert a second SystemSettings row with
            // a different Id — the check constraint will reject it.
            //
            // Note: the unique index on Id (below) ALSO prevents a second row,
            //   but only by uniqueness, not by value. The check constraint makes
            //   the rule explicit: "the singleton row must have Id = 0".
            //
            // SQL Server check constraint syntax for a uniqueidentifier column
            // compared against the all-zeros Guid:
            //   CHECK ([Id] = '00000000-0000-0000-0000-000000000000')
            //
            // API NOTE: EF Core 9+ deprecated the standalone
            //   builder.HasCheckConstraint(name, sql)
            // extension in favor of the model-builder lambda on ToTable:
            //   builder.ToTable("Name", t => t.HasCheckConstraint(name, sql))
            // The new API is the same underlying CHECK constraint; the
            // signature change is just to consolidate table-level configuration
            // in one place.
            // ------------------------------------------------------------------
            t.HasCheckConstraint(
                "CK_SystemSettings_Id_IsSingleton",
                "[Id] = '00000000-0000-0000-0000-000000000000'");
        });

        builder.HasKey(s => s.Id);

        // ------------------------------------------------------------------
        // VALUE GENERATION STRATEGY:
        //   EXPLICITLY tell EF Core that the Id is set by the application
        //   (SystemSettings.SingletonId == Guid.Empty) and that EF Core MUST
        //   NOT generate a value during SaveChanges.
        //
        //   WITHOUT this, EF Core's default for a Guid PK is
        //   ValueGeneratedOnAdd, which triggers SequentialGuidValueGenerator
        //   when the property value equals the CLR default (Guid.Empty).
        //   Since our singleton row IS Guid.Empty by design, EF Core would
        //   silently replace it with a freshly-generated Guid on INSERT,
        //   and the INSERT would then fail the
        //   CK_SystemSettings_Id_IsSingleton CHECK constraint below
        //   (which requires Id == '00000000-0000-0000-0000-000000000000').
        //
        //   ValueGeneratedNever is metadata-only — it doesn't change the
        //   database schema, just EF Core's runtime behaviour. The Id column
        //   stays a plain uniqueidentifier PK with the singleton CHECK
        //   constraint + unique index.
        // ------------------------------------------------------------------
        builder.Property(s => s.Id).ValueGeneratedNever();

        // ------------------------------------------------------------------
        // Scalar columns
        // ------------------------------------------------------------------
        // LimitMode stored as int (EF Core's default enum mapping).
        // No HasDefaultValue — the singleton row is always created via
        // SystemSettings.CreateDefault(), which sets LimitMode = CountOnly.
        builder.Property(s => s.LimitMode)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(s => s.UpdatedAt).IsRequired();

        // Unique index on Id — redundant with the PK, but explicit. Makes
        // it obvious at the schema level that this table can only ever have
        // one row.
        builder.HasIndex(s => s.Id).IsUnique();

        // ------------------------------------------------------------------
        // SEED DATA (Step 12-b runtime fix):
        //   Seed the singleton row at migration time so the
        //   SystemSettingsRepository.GetOrCreateAsync lazy-create path is
        //   NEVER hit on a fresh install. The lazy-create path has
        //   historically been a source of CHECK-constraint violations and
        //   "Sequence contains no elements" cascade failures (see
        //   SystemSettingsRepository.GetOrCreateAsync inline doc for the
        //   full forensic trail). Seeding eliminates the entire failure
        //   mode — the row exists by the time the app boots.
        //
        //   Values mirror SystemSettings.CreateDefault():
        //     - Id         = Guid.Empty (matches CK_SystemSettings_Id_IsSingleton)
        //     - LimitMode  = CountOnly (= 1; the fresh-install default)
        //     - UpdatedAt  = 2025-01-01T00:00:00Z (a fixed sentinel so
        //                    admins can tell "factory default, never
        //                    changed" from "actually updated just now"
        //                    when they look at the timestamp in the UI).
        //
        //   HasData is the EF Core idiomatic way to seed — it's reflected
        //   in the model snapshot so future migrations don't drift.
        //   Anonymous-typed to keep this configuration file decoupled
        //   from the SystemSettings.Load factory (which is a Domain-layer
        //   concern — Infrastructure shouldn't call Domain factories
        //   directly per the Clean Architecture layering rules).
        // ------------------------------------------------------------------
        builder.HasData(new
        {
            Id = Guid.Empty,
            LimitMode = LimitMode.CountOnly,
            UpdatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Common.Entities;

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
    }
}
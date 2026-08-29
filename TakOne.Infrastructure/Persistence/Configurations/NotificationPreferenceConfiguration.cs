using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Notifications.Entities;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="NotificationPreference"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>TABLE LAYOUT</b>:
///   <c>NotificationPreferences</c> table with:
///   <list type="bullet">
///     <item><c>Id</c> — PK, Guid, ValueGeneratedNever (the aggregate assigns
///         the Guid in its ctor).</item>
///     <item><c>UserId</c> — Guid, part of the unique (UserId, Kind) index.</item>
///     <item><c>Kind</c> — int column via HasConversion (enum stored as int
///         for index efficiency, matching <c>Notifications.Kind</c>).</item>
///     <item><c>IsMuted</c> — bit. Row presence + false = explicitly
///         un-muted (was muted before); row absence = default un-muted.</item>
///     <item><c>UpdatedAtUtc</c> — datetime2, diagnostic timestamp.</item>
///   </list>
/// </para>
/// <para>
/// <b>UNIQUE INDEX (one row per user per kind)</b>: composite
/// <c>(UserId, Kind)</c> — the upsert command handler relies on this to
/// keep the toggle idempotent under double-clicks / concurrent circuits
/// (the loser of the race fails with 2627 and the UI re-loads the truth
/// on next render).
/// </para>
/// <para>
/// <b>READ PATTERNS SERVED</b>:
///   <list type="bullet">
///     <item><c>IsMutedAsync(user, kind)</c> — single index seek on the
///         unique index.</item>
///     <item><c>GetAllForUserAsync(user)</c> — range scan on the same
///         index's leading column.</item>
///     <item><c>GetMutedUserIdsAsync(kind)</c> — a KIND-ONLY lookup. The
///         unique index is (UserId, Kind), so kind-first scans can't seek;
///         a second (non-unique) index on <c>Kind</c> would serve it, but
///         the fanout path calls it once per broadcast (rare) over a tiny
///         table (sparse rows), so a residual scan is the cheaper
///         trade-off — one fewer index to maintain on every toggle.</item>
///   </list>
/// </para>
/// <para>
/// <b>NO FK TO USERS</b>: deliberate — matches the
/// <c>NotificationConfiguration</c> convention (cross-aggregate references
/// are bare Guids, no navigations, no DB-level FK).
/// </para>
/// </remarks>
public sealed class NotificationPreferenceConfiguration
    : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("NotificationPreferences");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .ValueGeneratedNever();

        builder.Property(p => p.UserId)
            .IsRequired();

        builder.Property(p => p.Kind)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(p => p.IsMuted)
            .IsRequired();

        builder.Property(p => p.UpdatedAtUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        // ── INDEXES ──────────────────────────────────────────────────────

        // One row per user per kind — the upsert invariant.
        builder.HasIndex(p => new { p.UserId, p.Kind })
            .IsUnique()
            .HasDatabaseName("UX_NotificationPreferences_UserId_Kind");
    }
}

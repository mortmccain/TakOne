using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="BroadcastNotification"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>TABLE LAYOUT</b>:
///   <c>BroadcastNotifications</c> table with:
///   <list type="bullet">
///     <item><c>Id</c> — PK, Guid, ValueGeneratedNever (the aggregate assigns the Guid in its ctor).</item>
///     <item><c>SentByUserId</c> — Guid. Guid.Empty marks a system-emitted broadcast (no human author).</item>
///     <item><c>SentAtUtc</c> — datetime2, indexed (the admin audit list is ORDER BY SentAtUtc DESC).</item>
///     <item><c>Scope</c> — int column via HasConversion (the enum is stored as int for index efficiency).</item>
///     <item><c>TargetRoleName</c> — nullable nvarchar(50) (set iff Scope=Role; matches AspNetRoles.Name length).</item>
///     <item><c>TargetGroupId</c> — nullable Guid (set iff Scope=Group).</item>
///     <item><c>TargetUserId</c> — nullable Guid (set iff Scope=User).</item>
///     <item><c>Title</c> — nvarchar(200), required (matches the Notification.Title bound).</item>
///     <item><c>Message</c> — nvarchar(1000), required (matches the Notification.Message bound).</item>
///     <item><c>FanoutKind</c> — int column via HasConversion (Broadcast or AppUpdate).</item>
///     <item><c>RecipientCount</c> — int, required (0 is valid for empty audiences).</item>
///   </list>
/// </para>
/// <para>
/// <b>NO FK TO USERS / GROUPS</b>: deliberate (matches <see cref="NotificationConfiguration"/>'s
/// convention — cross-aggregate references are bare Guids, no navigation
/// properties, no FKs at the DB level). The Application layer enforces
/// the relationship; the DB only stores the snapshot. This also lets a
/// broadcast audit row survive the deletion of its target user/group
/// (the audit row's TargetUserId/TargetGroupId remain, but the
/// handler's name-resolver returns null for the missing entity — the
/// admin sees "[deleted user]" in the audit list).
/// </para>
/// <para>
/// <b>INDEXES</b>:
///   <list type="bullet">
///     <item><c>SentAtUtc</c> — supports the audit list's <c>ORDER BY SentAtUtc DESC OFFSET ... FETCH NEXT</c>.</item>
///     <item><c>FanoutKind</c> — supports the admin's filter "only system broadcasts" vs "only admin-authored" (low cardinality, but the filter is a UI feature so the index is a nicety, not a hot path).</item>
///   </list>
/// </para>
/// </remarks>
public sealed class BroadcastNotificationConfiguration : IEntityTypeConfiguration<BroadcastNotification>
{
    public void Configure(EntityTypeBuilder<BroadcastNotification> builder)
    {
        builder.ToTable("BroadcastNotifications");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id)
            .ValueGeneratedNever();

        builder.Property(b => b.SentByUserId)
            .IsRequired();

        builder.Property(b => b.SentAtUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(b => b.Scope)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(b => b.TargetRoleName)
            .HasMaxLength(50)
            .IsRequired(false);

        builder.Property(b => b.TargetGroupId)
            .IsRequired(false);

        builder.Property(b => b.TargetUserId)
            .IsRequired(false);

        builder.Property(b => b.Title)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(b => b.Message)
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(b => b.FanoutKind)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(b => b.RecipientCount)
            .IsRequired();

        // ── INDEXES ──────────────────────────────────────────────────────

        // Hot path: "list past broadcasts, newest-first" —
        // ORDER BY SentAtUtc DESC OFFSET @o ROWS FETCH NEXT @p ROWS ONLY.
        builder.HasIndex(b => b.SentAtUtc)
            .HasDatabaseName("IX_BroadcastNotifications_SentAtUtc");

        // Filter "only system broadcasts" (AppUpdate) vs "only admin-authored"
        // (Broadcast) on the admin audit page. Low cardinality (2 distinct
        // values), so this is a small index — the filter is a UI feature, not
        // a hot path.
        builder.HasIndex(b => b.FanoutKind)
            .HasDatabaseName("IX_BroadcastNotifications_FanoutKind");
    }
}

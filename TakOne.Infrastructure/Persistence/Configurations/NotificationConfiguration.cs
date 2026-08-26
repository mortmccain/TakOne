using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Notification"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>TABLE LAYOUT</b>:
///   <c>Notifications</c> table with:
///   <list type="bullet">
///     <item><c>Id</c> — PK, Guid, ValueGeneratedNever (the aggregate assigns the Guid in its ctor).</item>
///     <item><c>UserId</c> — indexed (every read query filters by this).</item>
///     <item><c>Kind</c> — int column via HasConversion (the enum is stored as int for index efficiency).</item>
///     <item><c>SaleId</c> — nullable Guid (future non-sale notifications have null here).</item>
///     <item><c>SaleDisplayNumber</c> — nullable nvarchar(64) (sale numbers are short — e.g. "INT-1505-00000042").</item>
///     <item><c>ActorName</c> — nullable nvarchar(200) (matches the User.FullName constraint).</item>
///     <item><c>Reason</c> — nullable nvarchar(500) (cancellation reason; bounded by the sale aggregate).</item>
///     <item><c>CreatedAtUtc</c> — datetime2, indexed (most UIs ORDER BY CreatedAtUtc DESC).</item>
///     <item><c>ReadAtUtc</c> — nullable datetime2, indexed (the unread-count query is
///         <c>WHERE UserId = @u AND ReadAtUtc IS NULL</c> — a filtered index makes this a fast seek).</item>
///   </list>
/// </para>
/// <para>
/// <b>UNIQUE INDEX (deduplication)</b>: composite
/// <c>(UserId, SaleId, Kind)</c> with a filter <c>WHERE SaleId IS NOT NULL</c>
/// — guards against the at-least-once delivery of Wolverine's transactional
/// outbox redelivering a sale-lifecycle event. Future non-sale notifications
/// (with null SaleId) are exempt from this dedup guard.
/// </para>
/// <para>
/// <b>READ-STATE FILTERED INDEX</b>: <c>(UserId, ReadAtUtc) WHERE ReadAtUtc IS NULL</c>
/// — the unread-count query is the hot path (called on every page load via
/// the bell icon badge). The filtered index makes it a tiny index seek.
/// </para>
/// <para>
/// <b>NO FK TO USERS / SALES</b>: deliberate (matches <c>SaleConfiguration</c>'s
/// convention — cross-aggregate references are bare Guids, no navigation
/// properties, no FKs at the DB level). The Application layer enforces the
/// relationship; the DB only stores the snapshot.
/// </para>
/// </remarks>
public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id)
            .ValueGeneratedNever();

        builder.Property(n => n.UserId)
            .IsRequired();

        builder.Property(n => n.Kind)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(n => n.SaleId)
            .IsRequired(false);

        builder.Property(n => n.SaleDisplayNumber)
            .HasMaxLength(64)
            .IsRequired(false);

        builder.Property(n => n.ActorName)
            .HasMaxLength(200)
            .IsRequired(false);

        builder.Property(n => n.Reason)
            .HasMaxLength(500)
            .IsRequired(false);

        // ── BROADCAST FANOUT FIELDS ──
        // Title + Message are populated by Notification.CreateBroadcast()
        // (admin-authored broadcast or system-emitted AppUpdate). Null for
        // sale-lifecycle notifications. BroadcastId is the back-pointer to
        // the parent BroadcastNotification aggregate's Id.
        builder.Property(n => n.Title)
            .HasMaxLength(200)
            .IsRequired(false);

        builder.Property(n => n.Message)
            .HasMaxLength(1000)
            .IsRequired(false);

        builder.Property(n => n.BroadcastId)
            .IsRequired(false);

        builder.Property(n => n.CreatedAtUtc)
            .HasColumnType("datetime2")
            .IsRequired();

        builder.Property(n => n.ReadAtUtc)
            .HasColumnType("datetime2")
            .IsRequired(false);

        // ── INDEXES ──────────────────────────────────────────────────────

        // Hot path: "list my notifications, newest first" —
        // WHERE UserId = @u ORDER BY CreatedAtUtc DESC OFFSET @o ROWS FETCH NEXT @p ROWS ONLY.
        builder.HasIndex(n => new { n.UserId, n.CreatedAtUtc })
            .HasDatabaseName("IX_Notifications_UserId_CreatedAtUtc");

        // Hot path: "unread count for bell badge" —
        // WHERE UserId = @u AND ReadAtUtc IS NULL. Filtered index makes
        // this a tiny index seek — only unread rows are in the index.
        builder.HasIndex(n => new { n.UserId, n.ReadAtUtc })
            .HasDatabaseName("IX_Notifications_UserId_ReadAtUtc_Unread")
            .HasFilter("[ReadAtUtc] IS NULL");

        // Deduplication guard: at-least-once delivery from the Wolverine
        // outbox can redeliver the same domain event; the second INSERT
        // for the same (UserId, SaleId, Kind) tuple fails with a 2627/2601
        // unique-constraint violation, which the retry loop catches.
        // Filtered to SaleId IS NOT NULL so future non-sale notifications
        // (with null SaleId) are exempt.
        builder.HasIndex(n => new { n.UserId, n.SaleId, n.Kind })
            .IsUnique()
            .HasDatabaseName("UX_Notifications_UserId_SaleId_Kind")
            .HasFilter("[SaleId] IS NOT NULL");

        // ── BROADCAST CORRELATION INDEX ──
        // Lets the admin's "broadcast detail" page (future) find all
        // per-user fanout rows for a given BroadcastNotification.Id in a
        // single index seek. Nullable column, so unfiltered index covers
        // all rows; the WHERE BroadcastId IS NOT NULL filter would make
        // it a smaller filtered index but the admin's detail-page query
        // is rare enough that the unfiltered index's slightly larger size
        // is acceptable (and it doubles as a fanout-row existence check).
        builder.HasIndex(n => n.BroadcastId)
            .HasDatabaseName("IX_Notifications_BroadcastId")
            .HasFilter("[BroadcastId] IS NOT NULL");
    }
}

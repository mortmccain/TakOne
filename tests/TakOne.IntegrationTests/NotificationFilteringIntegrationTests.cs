using FluentAssertions;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the NotificationRepository's Round 4 additions:
/// the per-kind filter on <see cref="INotificationRepository.GetPaginatedForUserAsync"/>
/// and the scoped hard-delete <see cref="INotificationRepository.DeleteForUserAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY REAL-DB TESTS</b>: both methods' guarantees are DATABASE
/// guarantees the mocks can't verify — the Kind predicate must prune
/// rows IN SQL with an accurate TotalCount, and the delete's user-id
/// scoping must ride in the DELETE predicate (a foreign id deletes
/// zero rows).
/// </para>
/// </remarks>
public class NotificationFilteringIntegrationTests
{
    private static async Task<(NotificationRepository repo, ApplicationDbContext db)> CreateSeededAsync(
        params Notification[] notifications)
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var repo = new NotificationRepository(db);

        foreach (var n in notifications)
        {
            await repo.AddAsync(n, CancellationToken.None);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        return (repo, db);
    }

    private static Notification Make(
        NotificationKind kind,
        Guid? userId = null,
        Guid? saleId = null) =>
        Notification.Create(
            userId ?? TestValues.UserId,
            kind,
            saleId,
            saleId is null ? null : $"INT-1405-{saleId.Value.ToString()[..4]}",
            actorName: "Actor",
            reason: null);

    // ── Per-kind filter ──────────────────────────────────────────────

    [Fact]
    public async Task GetPaginatedForUserAsync_WithKind_ReturnsOnlyThatKind()
    {
        // Arrange — three kinds for the user + one Broadcast for someone else.
        var submitted = Make(NotificationKind.SaleSubmitted, saleId: Guid.NewGuid());
        var approved = Make(NotificationKind.SaleApproved, saleId: Guid.NewGuid());
        var broadcast = Make(NotificationKind.Broadcast);
        var foreign = Make(NotificationKind.Broadcast, userId: Guid.NewGuid());

        var (repo, db) = await CreateSeededAsync(submitted, approved, broadcast, foreign);
        await using (db)
        {
            // Act
            var result = await repo.GetPaginatedForUserAsync(
                TestValues.UserId, pageNumber: 1, pageSize: 20,
                unreadOnly: false, kind: NotificationKind.Broadcast);

            // Assert — only the user's OWN Broadcast rows.
            result.TotalCount.Should().Be(1,
                "the kind filter composes with the user scope (the foreign row never appears)");
            result.Items.Should().ContainSingle(n => n.Id == broadcast.Id);
        }
    }

    [Fact]
    public async Task GetPaginatedForUserAsync_WithKindAndUnread_FiltersCompose()
    {
        // Arrange — two Broadcasts: one read, one unread.
        var unread = Make(NotificationKind.Broadcast);
        var read = Make(NotificationKind.Broadcast);
        read.MarkAsRead();
        var otherKind = Make(NotificationKind.AppUpdate, saleId: Guid.NewGuid());

        var (repo, db) = await CreateSeededAsync(unread, read, otherKind);
        await using (db)
        {
            // Act
            var result = await repo.GetPaginatedForUserAsync(
                TestValues.UserId, pageNumber: 1, pageSize: 20,
                unreadOnly: true, kind: NotificationKind.Broadcast);

            // Assert — kind ∩ unread.
            result.TotalCount.Should().Be(1);
            result.Items.Should().ContainSingle(n => n.Id == unread.Id);
        }
    }

    [Fact]
    public async Task GetPaginatedForUserAsync_WithoutKind_ReturnsAllKinds()
    {
        // Backward compatibility: null kind = no clause.
        var a = Make(NotificationKind.SaleSubmitted, saleId: Guid.NewGuid());
        var b = Make(NotificationKind.GroupChanged);

        var (repo, db) = await CreateSeededAsync(a, b);
        await using (db)
        {
            var result = await repo.GetPaginatedForUserAsync(
                TestValues.UserId, pageNumber: 1, pageSize: 20,
                unreadOnly: false, kind: null);

            result.TotalCount.Should().Be(2);
        }
    }

    // ── Scoped hard delete ───────────────────────────────────────────

    [Fact]
    public async Task DeleteForUserAsync_OwnNotification_DeletesAndReportsTrue()
    {
        // Arrange
        var mine = Make(NotificationKind.SaleSubmitted, saleId: Guid.NewGuid());
        var (repo, db) = await CreateSeededAsync(mine);
        await using (db)
        {
            // Act
            var deleted = await repo.DeleteForUserAsync(mine.Id, TestValues.UserId);

            // Assert — the row is GONE (and the unread count follows).
            deleted.Should().BeTrue();
            db.ChangeTracker.Clear();
            (await repo.GetByIdForUserAsync(mine.Id, TestValues.UserId))
                .Should().BeNull();
            (await repo.GetUnreadCountAsync(TestValues.UserId)).Should().Be(0);
        }
    }

    [Fact]
    public async Task DeleteForUserAsync_ForeignNotification_ReportsFalseAndKeepsRow()
    {
        // Arrange — the notification belongs to ANOTHER user.
        var foreign = Make(NotificationKind.Broadcast, userId: Guid.NewGuid());
        var (repo, db) = await CreateSeededAsync(foreign);
        await using (db)
        {
            // Act — the caller tries to dismiss it.
            var deleted = await repo.DeleteForUserAsync(foreign.Id, TestValues.UserId);

            // Assert — zero rows deleted; the owner's row survives.
            deleted.Should().BeFalse("the user scope rides in the DELETE predicate");
            (await repo.GetByIdForUserAsync(foreign.Id, foreign.UserId))
                .Should().NotBeNull("the owner's notification is untouched");
        }
    }

    [Fact]
    public async Task DeleteForUserAsync_MissingId_ReportsFalse()
    {
        var (repo, db) = await CreateSeededAsync(Make(NotificationKind.AppUpdate, saleId: Guid.NewGuid()));
        await using (db)
        {
            var deleted = await repo.DeleteForUserAsync(Guid.NewGuid(), TestValues.UserId);
            deleted.Should().BeFalse();
        }
    }
}

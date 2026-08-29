using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="NotificationPreferenceRepository"/>
/// (Round 3 — per-user, per-kind notification mute flags).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY REAL-DB TESTS (vs. NSubstitute mocks)</b>: the repository's
/// contract depends on DATABASE guarantees the mocks can't verify:
/// <list type="bullet">
///   <item>The <c>UX_NotificationPreferences_UserId_Kind</c> unique index —
///       a duplicate INSERT must throw (the upsert handler's race-loser
///       path relies on it).</item>
///   <item>The sparse-scope filtering — <c>IsMutedAsync</c> must read
///       false for OTHER users' rows, and <c>GetAllForUserAsync</c> must
///       never leak another user's rows.</item>
///   <item><c>GetMutedUserIdsAsync</c> must include ONLY users whose row
///       has IsMuted=true for THAT kind (an un-muted leftover row from a
///       previous toggle must NOT appear in the muted set).</item>
///   <item>The tracked-vs-no-tracking contract — GetForUserAsync returns
///       a TRACKED entity whose Mute()/Unmute() mutations are flushed by
///       the next SaveChangesAsync.</item>
/// </list>
/// </para>
/// </remarks>
public class NotificationPreferenceRepositoryTests
{
    private static async Task<ApplicationDbContext> CreateDbAsync()
        => await SqliteTestDbFactory.CreateAsync();

    // ── IsMutedAsync (the suppression hot path) ─────────────────────────

    [Fact]
    public async Task IsMutedAsync_NoRow_ReturnsFalse()
    {
        // Sparse default: a user who never muted anything has NO rows —
        // this is the steady state for most users and the answer the
        // NotifyOn* handlers expect.
        await using var db = await CreateDbAsync();
        var repo = new NotificationPreferenceRepository(db);

        var isMuted = await repo.IsMutedAsync(
            TestValues.UserId, NotificationKind.SaleSubmitted);

        isMuted.Should().BeFalse();
    }

    [Fact]
    public async Task IsMutedAsync_MutedRow_ReturnsTrue()
    {
        await using var db = await CreateDbAsync();
        var repo = new NotificationPreferenceRepository(db);

        await repo.AddAsync(NotificationPreference.Create(
            TestValues.UserId, NotificationKind.SaleSubmitted, isMuted: true));
        await db.SaveChangesAsync();

        (await repo.IsMutedAsync(TestValues.UserId, NotificationKind.SaleSubmitted))
            .Should().BeTrue();
    }

    [Fact]
    public async Task IsMutedAsync_IsScopedPerUserAndPerKind()
    {
        // Arrange — user A muted kind X; user B muted kind Y. Each query
        // must see ONLY its own (user, kind) cell.
        await using var db = await CreateDbAsync();
        var repo = new NotificationPreferenceRepository(db);

        await repo.AddAsync(NotificationPreference.Create(
            TestValues.UserId, NotificationKind.SaleSubmitted, isMuted: true));
        await repo.AddAsync(NotificationPreference.Create(
            TestValues.CustomerId, NotificationKind.Broadcast, isMuted: true));
        await db.SaveChangesAsync();

        // Act + Assert
        (await repo.IsMutedAsync(TestValues.UserId, NotificationKind.Broadcast))
            .Should().BeFalse("user A muted a DIFFERENT kind");
        (await repo.IsMutedAsync(TestValues.CustomerId, NotificationKind.SaleSubmitted))
            .Should().BeFalse("user B muted a DIFFERENT kind");
        (await repo.IsMutedAsync(TestValues.CreatedByUserId, NotificationKind.SaleSubmitted))
            .Should().BeFalse("user C has no rows at all");
    }

    [Fact]
    public async Task IsMutedAsync_UnmutedLeftoverRow_ReturnsFalse()
    {
        // A row that exists with IsMuted=false (was muted, then un-muted —
        // the row is kept per the sparse-storage semantics) must read as
        // NOT muted.
        await using var db = await CreateDbAsync();
        var repo = new NotificationPreferenceRepository(db);

        var preference = NotificationPreference.Create(
            TestValues.UserId, NotificationKind.AppUpdate, isMuted: true);
        await repo.AddAsync(preference);
        await db.SaveChangesAsync();

        preference.Unmute();
        await db.SaveChangesAsync();

        (await repo.IsMutedAsync(TestValues.UserId, NotificationKind.AppUpdate))
            .Should().BeFalse();
    }

    // ── GetAllForUserAsync (the settings-page load) ─────────────────────

    [Fact]
    public async Task GetAllForUserAsync_ReturnsOnlyThatUsersRows()
    {
        await using var db = await CreateDbAsync();
        var repo = new NotificationPreferenceRepository(db);

        await repo.AddAsync(NotificationPreference.Create(
            TestValues.UserId, NotificationKind.SaleCancelled, isMuted: true));
        await repo.AddAsync(NotificationPreference.Create(
            TestValues.UserId, NotificationKind.GroupChanged, isMuted: true));
        await repo.AddAsync(NotificationPreference.Create(
            TestValues.CustomerId, NotificationKind.Broadcast, isMuted: true));
        await db.SaveChangesAsync();

        var rows = await repo.GetAllForUserAsync(TestValues.UserId);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(p => p.UserId == TestValues.UserId);
    }

    // ── GetForUserAsync (the upsert load-for-mutation path) ─────────────

    [Fact]
    public async Task GetForUserAsync_ReturnsNullWhenNoRow()
    {
        await using var db = await CreateDbAsync();
        var repo = new NotificationPreferenceRepository(db);

        (await repo.GetForUserAsync(TestValues.UserId, NotificationKind.Broadcast))
            .Should().BeNull();
    }

    [Fact]
    public async Task GetForUserAsync_ReturnsTrackedEntity_WhoseMutationsPersist()
    {
        // The upsert handler relies on the change tracker: it calls
        // Mute()/Unmute() on the returned entity and the NEXT
        // SaveChangesAsync flushes it. If the repo accidentally returned
        // an AsNoTracking entity, the toggle would silently no-op — this
        // test locks the tracked contract in.
        await using var db = await CreateDbAsync();
        var repo = new NotificationPreferenceRepository(db);

        await repo.AddAsync(NotificationPreference.Create(
            TestValues.UserId, NotificationKind.SaleInvoiced, isMuted: false));
        await db.SaveChangesAsync();

        var loaded = await repo.GetForUserAsync(
            TestValues.UserId, NotificationKind.SaleInvoiced);
        loaded.Should().NotBeNull();
        loaded!.IsMuted.Should().BeFalse();

        loaded.Mute();
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        (await repo.IsMutedAsync(TestValues.UserId, NotificationKind.SaleInvoiced))
            .Should().BeTrue("the tracked entity's Mute() must persist");
    }

    // ── GetMutedUserIdsAsync (the broadcast fanout batch skip) ──────────

    [Fact]
    public async Task GetMutedUserIdsAsync_ReturnsOnlyMutedUsersForThatKind()
    {
        await using var db = await CreateDbAsync();
        var repo = new NotificationPreferenceRepository(db);

        // Two users muted Broadcast; one has an un-muted leftover row for
        // the same kind; another muted a DIFFERENT kind entirely.
        await repo.AddAsync(NotificationPreference.Create(
            TestValues.UserId, NotificationKind.Broadcast, isMuted: true));
        await repo.AddAsync(NotificationPreference.Create(
            TestValues.CustomerId, NotificationKind.Broadcast, isMuted: true));
        await repo.AddAsync(NotificationPreference.Create(
            TestValues.CreatedByUserId, NotificationKind.Broadcast, isMuted: false));
        await repo.AddAsync(NotificationPreference.Create(
            TestValues.GroupId, NotificationKind.AppUpdate, isMuted: true));
        await db.SaveChangesAsync();

        var muted = await repo.GetMutedUserIdsAsync(NotificationKind.Broadcast);

        muted.Should().BeEquivalentTo(new[] { TestValues.UserId, TestValues.CustomerId });
    }

    // ── Unique-index enforcement (the upsert race-loser safety net) ─────

    [Fact]
    public async Task AddAsync_DuplicateUserKindTuple_Throws()
    {
        // The UX_NotificationPreferences_UserId_Kind unique index is the
        // concurrency safety net for double-click/two-tab mutes: both
        // see "no row", both INSERT, the loser gets a clean constraint
        // violation inside the transaction. If this test ever fails, the
        // index wiring was lost.
        await using var db = await CreateDbAsync();
        var repo = new NotificationPreferenceRepository(db);

        await repo.AddAsync(NotificationPreference.Create(
            TestValues.UserId, NotificationKind.SaleSubmitted, isMuted: true));
        await db.SaveChangesAsync();

        await repo.AddAsync(NotificationPreference.Create(
            TestValues.UserId, NotificationKind.SaleSubmitted, isMuted: true));

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}

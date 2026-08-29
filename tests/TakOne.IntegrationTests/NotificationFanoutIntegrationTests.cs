using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Commands.EmitAppUpdateBroadcast;
using TakOne.Application.Notifications.Commands.SendBroadcastNotification;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Domain.Users;
using TakOne.Infrastructure.Identity;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.Infrastructure.Services;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the broadcast-notification fanout pipeline.
/// Drives both the system-emitted app-update path
/// (<see cref="EmitAppUpdateBroadcastCommandHandler"/>) and the admin-
/// authored path (<see cref="SendBroadcastNotificationCommandHandler"/>)
/// against a real EF Core + SQLite DB. Verifies that the audit row + N
/// per-user fanout rows persist atomically in the same transaction, that
/// the idempotency dedup works for app-update redelivery, and that
/// scope-target resolution fans out to the right users.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THESE TESTS EXIST (the gap they fill):</b> the mock-heavy
/// handler unit tests verify the handler called the right repo methods
/// with the right args — but they can't catch:
/// <list type="bullet">
///   <item>FK violations (e.g. a fanout Notification row referencing a
///       non-existent BroadcastId)</item>
///   <item>The transactional atomicity contract — if SaveChanges fails
///       halfway through the fanout, NO row should be left dangling.</item>
///   <item>The idempotency dedup actually returning the original
///       RecipientCount and NOT creating a duplicate audit row.</item>
///   <item>The scope-target resolution actually resolving the right
///       user Ids (e.g. Scope=Role actually filters by role + active).</item>
/// </list>
/// These integration tests use a real DB to catch all of those.
/// </para>
/// </remarks>
public class NotificationFanoutIntegrationTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private const string CustomerName = "John Customer";

    // Build the real-DB collaborator tuple for both broadcast handlers.
    private static async Task<(
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        IBroadcastNotificationRepository broadcastRepo,
        INotificationRepository notificationRepo,
        INotificationPreferenceRepository preferenceRepo,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db,
        ILogger<EmitAppUpdateBroadcastCommandHandler> emitLogger,
        ILogger<SendBroadcastNotificationCommandHandler> sendLogger)>
        BuildWiredCollaboratorsAsync()
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var userRepo = new UserRepository(db);
        var groupRepo = new CustomerGroupRepository(db);
        var broadcastRepo = new BroadcastNotificationRepository(db);
        var notificationRepo = new NotificationRepository(db);
        // Round 3 — real preference repository against the same SQLite DB
        // (default: no rows = nobody muted, matching production defaults).
        var preferenceRepo = new NotificationPreferenceRepository(db);
        var unitOfWork = new UnitOfWork(db);

        var emitLogger = Substitute.For<ILogger<EmitAppUpdateBroadcastCommandHandler>>();
        var sendLogger = Substitute.For<ILogger<SendBroadcastNotificationCommandHandler>>();

        return (userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo,
            unitOfWork, db, emitLogger, sendLogger);
    }

    // Seed N active DomainUsers + (optionally) one inactive DomainUser.
    // Returns the Guids of the ACTIVE seeded users (so the test can assert
    // RecipientCount == that list's count). If groupId is null, a default
    // CustomerGroup is seeded first (the User.GroupId column is a real FK
    // to CustomerGroups.Id with OnDelete.Restrict — seeding a User without
    // the group existing would fail the FK constraint on SQLite).
    private static async Task<List<Guid>> SeedActiveDomainUsersAsync(
        ApplicationDbContext db,
        int activeCount,
        int inactiveCount = 0,
        Guid? groupId = null)
    {
        Guid resolvedGroupId;
        if (groupId is null)
        {
            // Seed a default CustomerGroup so the FK is satisfied.
            var group = CustomerGroup.Create(
                "Default Test Group",
                new Money(1000m, TestValues.IRR));
            db.CustomerGroups.Add(group);
            await db.SaveChangesAsync();
            resolvedGroupId = group.Id;
        }
        else
        {
            resolvedGroupId = groupId.Value;
        }

        for (var i = 0; i < activeCount; i++)
        {
            var id = Guid.NewGuid();
            db.DomainUsers.Add(User.CreateCustomer(
                workerId: $"EMP-{id.ToString("N").Substring(0, 8)}",
                fullName: $"User {i}",
                groupId: resolvedGroupId,
                gender: Gender.Male));
        }
        for (var i = 0; i < inactiveCount; i++)
        {
            var u = User.CreateCustomer(
                workerId: $"INACT-{i}-{Guid.NewGuid().ToString("N").Substring(0, 8)}",
                fullName: $"Inactive User {i}",
                groupId: resolvedGroupId,
                gender: Gender.Male);
            u.Deactivate();
            db.DomainUsers.Add(u);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Reload the active users' Ids from the DB so the test can use them.
        var activeUsers = db.DomainUsers.Where(u => u.IsActive).ToList();
        return activeUsers.Select(u => u.Id).ToList();
    }

    // Seed ApplicationUser + IdentityRole + IdentityUserRole rows so the
    // role-targeted broadcast query (which joins the Identity tables)
    // can resolve the right users.
    private static async Task SeedIdentityRoleAssignmentAsync(
        ApplicationDbContext db,
        IReadOnlyList<Guid> userIds,
        string roleName)
    {
        // Create or look up the role row.
        var role = db.Roles.FirstOrDefault(r => r.Name == roleName);
        if (role is null)
        {
            role = new IdentityRole<Guid>
            {
                Id = Guid.NewGuid(),
                Name = roleName,
                NormalizedName = roleName.ToUpperInvariant()
            };
            db.Roles.Add(role);
        }

        foreach (var userId in userIds)
        {
            // Create the ApplicationUser with the same Id as the Domain User
            // (shared PK convention — see ApplicationUser.cs class-level docs).
            // Only UserName is required (NOT NULL column on AspNetUsers).
            var appUser = new ApplicationUser
            {
                Id = userId,
                UserName = $"user-{userId.ToString("N").Substring(0, 8)}",
                IsActive = true
            };
            db.Users.Add(appUser);
            db.UserRoles.Add(new IdentityUserRole<Guid>
            {
                UserId = userId,
                RoleId = role.Id
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ── Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task EmitAppUpdateBroadcast_WithNoExistingAudits_PersistsBroadcastAndFanoutRows()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            // Seed 3 active DomainUsers so Scope=All resolves 3 recipients.
            await SeedActiveDomainUsersAsync(collaborators.db, activeCount: 3);

            // Act
            var result = await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
                new EmitAppUpdateBroadcastCommand(
                    Title: "TakOne updated to v1.0.0",
                    Message: "Please reload the page to get the new version."),
                collaborators.userRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.emitLogger,
                CancellationToken.None);

            // Assert — handler returns success with RecipientCount=3.
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be(3);

            // Reload from DB (clear tracker) and verify persisted state.
            collaborators.db.ChangeTracker.Clear();

            // 1 BroadcastNotification row with SentByUserId=Guid.Empty,
            // FanoutKind=AppUpdate, RecipientCount=3.
            var broadcasts = collaborators.db.BroadcastNotifications
                .AsNoTracking().ToList();
            broadcasts.Should().HaveCount(1);
            var broadcast = broadcasts[0];
            broadcast.SentByUserId.Should().Be(Guid.Empty);
            broadcast.FanoutKind.Should().Be(NotificationKind.AppUpdate);
            broadcast.Scope.Should().Be(BroadcastScope.All);
            broadcast.RecipientCount.Should().Be(3);
            broadcast.Title.Should().Be("TakOne updated to v1.0.0");

            // 3 Notification fanout rows (one per active user), all with
            // Kind=AppUpdate, BroadcastId=broadcast.Id, Title copied verbatim.
            var fanouts = collaborators.db.Notifications
                .AsNoTracking().Where(n => n.BroadcastId == broadcast.Id).ToList();
            fanouts.Should().HaveCount(3);
            fanouts.Should().OnlyContain(n => n.Kind == NotificationKind.AppUpdate);
            fanouts.Should().OnlyContain(n => n.Title == "TakOne updated to v1.0.0");
            fanouts.Should().OnlyContain(n => n.ReadAtUtc == null);
        }
    }

    // Verifies the Wolverine redelivery dedup: a second call with the SAME
    // title returns the existing RecipientCount and does NOT create a
    // second audit row or new fanout rows.
    [Fact]
    public async Task EmitAppUpdateBroadcast_WithExistingAuditForSameTitle_SkipsFanoutAndReturnsExistingCount()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            await SeedActiveDomainUsersAsync(collaborators.db, activeCount: 3);

            // Act — first call.
            var firstResult = await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
                new EmitAppUpdateBroadcastCommand(
                    Title: "TakOne updated to v2.0.0",
                    Message: "Initial broadcast."),
                collaborators.userRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.emitLogger,
                CancellationToken.None);
            firstResult.IsSuccess.Should().BeTrue();
            firstResult.Value.Should().Be(3);

            // Second call with the SAME title — must dedup-hit, no new fanouts.
            var secondResult = await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
                new EmitAppUpdateBroadcastCommand(
                    Title: "TakOne updated to v2.0.0",
                    Message: "Wolverine redelivery attempt."),
                collaborators.userRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.emitLogger,
                CancellationToken.None);

            // Assert — second call returns the original RecipientCount and
            // does NOT insert a second audit row or new fanout rows.
            secondResult.IsSuccess.Should().BeTrue();
            secondResult.Value.Should().Be(3);

            collaborators.db.ChangeTracker.Clear();
            collaborators.db.BroadcastNotifications.AsNoTracking().ToList()
                .Should().HaveCount(1);
            collaborators.db.Notifications.AsNoTracking().Where(n =>
                n.Title == "TakOne updated to v2.0.0").ToList()
                .Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task EmitAppUpdateBroadcast_WithDifferentTitle_CreatesSecondBroadcast()
    {
        // Arrange
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            await SeedActiveDomainUsersAsync(collaborators.db, activeCount: 3);

            // Act — first call with title=v1.
            await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
                new EmitAppUpdateBroadcastCommand("v1", "first"),
                collaborators.userRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.emitLogger,
                CancellationToken.None);

            // Second call with DIFFERENT title — no dedup, creates second broadcast.
            await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
                new EmitAppUpdateBroadcastCommand("v2", "second"),
                collaborators.userRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.emitLogger,
                CancellationToken.None);

            // Assert — 2 broadcasts + 6 fanout rows (3 + 3).
            collaborators.db.ChangeTracker.Clear();
            collaborators.db.BroadcastNotifications.AsNoTracking().ToList()
                .Should().HaveCount(2);
            collaborators.db.Notifications.AsNoTracking().Where(n =>
                n.Title == "v1" || n.Title == "v2").ToList()
                .Should().HaveCount(6);
        }
    }

    [Fact]
    public async Task SendBroadcastNotification_WithScopeAll_FansOutToAllActiveUsers()
    {
        // Arrange — seed 3 active + 1 inactive DomainUser.
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            await SeedActiveDomainUsersAsync(collaborators.db, activeCount: 3, inactiveCount: 1);

            var currentUser = new CurrentUserHelper(
                userId: TestValues.CreatedByUserId,
                isAuthenticated: true,
                fullName: "Admin User",
                groupId: null,
                roles: Roles.Admin);

            // Act
            var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
                new SendBroadcastNotificationCommand(
                    Title: "System maintenance",
                    Message: "Brief downtime expected at 02:00 UTC.",
                    Scope: BroadcastScope.All,
                    TargetRoleName: null,
                    TargetGroupId: null,
                    TargetUserId: null),
                currentUser,
                collaborators.userRepo,
                collaborators.groupRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.sendLogger,
                CancellationToken.None);

            // Assert — RecipientCount=3 (inactive user excluded).
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be(3);

            collaborators.db.ChangeTracker.Clear();
            var broadcasts = collaborators.db.BroadcastNotifications.AsNoTracking().ToList();
            broadcasts.Should().HaveCount(1);
            broadcasts[0].RecipientCount.Should().Be(3);
            broadcasts[0].FanoutKind.Should().Be(NotificationKind.Broadcast);
            broadcasts[0].SentByUserId.Should().Be(TestValues.CreatedByUserId);

            // Fanout rows: 3 (one per active user).
            collaborators.db.Notifications.AsNoTracking()
                .Where(n => n.BroadcastId == broadcasts[0].Id).ToList()
                .Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task SendBroadcastNotification_WithScopeRole_FansOutToUsersInRole()
    {
        // Arrange — seed 3 DomainUsers, then assign 2 of them to the
        // "Customer" role in the Identity tables.
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var activeIds = await SeedActiveDomainUsersAsync(collaborators.db, activeCount: 3);
            var customerUserIds = activeIds.Take(2).ToList();
            await SeedIdentityRoleAssignmentAsync(collaborators.db, customerUserIds, Roles.Customer);

            var currentUser = new CurrentUserHelper(
                TestValues.CreatedByUserId, isAuthenticated: true,
                fullName: "Admin", groupId: null, roles: Roles.Admin);

            // Act
            var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
                new SendBroadcastNotificationCommand(
                    Title: "Customer-only announcement",
                    Message: "New product line available.",
                    Scope: BroadcastScope.Role,
                    TargetRoleName: Roles.Customer,
                    TargetGroupId: null,
                    TargetUserId: null),
                currentUser,
                collaborators.userRepo,
                collaborators.groupRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.sendLogger,
                CancellationToken.None);

            // Assert — RecipientCount = 2 (only the 2 users with Customer role).
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be(2);

            collaborators.db.ChangeTracker.Clear();
            var broadcast = collaborators.db.BroadcastNotifications.AsNoTracking().First();
            broadcast.RecipientCount.Should().Be(2);
            broadcast.Scope.Should().Be(BroadcastScope.Role);
            broadcast.TargetRoleName.Should().Be(Roles.Customer);
            collaborators.db.Notifications.AsNoTracking()
                .Where(n => n.BroadcastId == broadcast.Id).ToList()
                .Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task SendBroadcastNotification_WithScopeGroup_FansOutToUsersInGroup()
    {
        // Arrange — seed 2 CustomerGroups (A + B) + 3 DomainUsers in group A
        // + 2 DomainUsers in group B. SendBroadcast(Scope=Group, TargetGroupId=A.Id)
        // should resolve to the 3 users in group A.
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            // Seed two CustomerGroups — needed because the handler's defense-
            // in-depth check verifies the group exists before fanout.
            var groupA = CustomerGroup.Create("Group A", new Money(1000m, TestValues.IRR));
            var groupB = CustomerGroup.Create("Group B", new Money(500m, TestValues.IRR));
            collaborators.db.CustomerGroups.Add(groupA);
            collaborators.db.CustomerGroups.Add(groupB);
            await collaborators.db.SaveChangesAsync();

            // Seed 3 active users in group A (use groupA.Id as their groupId).
            var idsA = await SeedActiveDomainUsersAsync(collaborators.db, activeCount: 3, groupId: groupA.Id);
            // Seed 2 active users in group B.
            await SeedActiveDomainUsersAsync(collaborators.db, activeCount: 2, groupId: groupB.Id);

            var currentUser = new CurrentUserHelper(
                TestValues.CreatedByUserId, isAuthenticated: true,
                fullName: "Admin", groupId: null, roles: Roles.Admin);

            // Act
            var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
                new SendBroadcastNotificationCommand(
                    Title: "Group A message",
                    Message: "Hello group A members!",
                    Scope: BroadcastScope.Group,
                    TargetRoleName: null,
                    TargetGroupId: groupA.Id,
                    TargetUserId: null),
                currentUser,
                collaborators.userRepo,
                collaborators.groupRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.sendLogger,
                CancellationToken.None);

            // Assert — RecipientCount = 3 (only the users in group A).
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be(3);

            collaborators.db.ChangeTracker.Clear();
            var broadcast = collaborators.db.BroadcastNotifications.AsNoTracking().First();
            broadcast.RecipientCount.Should().Be(3);
            broadcast.Scope.Should().Be(BroadcastScope.Group);
            broadcast.TargetGroupId.Should().Be(groupA.Id);
            collaborators.db.Notifications.AsNoTracking()
                .Where(n => n.BroadcastId == broadcast.Id).ToList()
                .Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task SendBroadcastNotification_WithScopeUser_FansOutToSingleActiveUser()
    {
        // Arrange — seed one active DomainUser. SendBroadcast(Scope=User,
        // TargetUserId=that user) should fan out to exactly 1 user.
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            var activeIds = await SeedActiveDomainUsersAsync(collaborators.db, activeCount: 1);
            var targetId = activeIds[0];

            var currentUser = new CurrentUserHelper(
                TestValues.CreatedByUserId, isAuthenticated: true,
                fullName: "Admin", groupId: null, roles: Roles.Admin);

            // Act
            var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
                new SendBroadcastNotificationCommand(
                    Title: "Personal message",
                    Message: "Just for you.",
                    Scope: BroadcastScope.User,
                    TargetRoleName: null,
                    TargetGroupId: null,
                    TargetUserId: targetId),
                currentUser,
                collaborators.userRepo,
                collaborators.groupRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.sendLogger,
                CancellationToken.None);

            // Assert — RecipientCount=1, one fanout row, that row's UserId=targetId.
            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be(1);

            collaborators.db.ChangeTracker.Clear();
            var broadcast = collaborators.db.BroadcastNotifications.AsNoTracking().First();
            broadcast.RecipientCount.Should().Be(1);
            broadcast.Scope.Should().Be(BroadcastScope.User);
            broadcast.TargetUserId.Should().Be(targetId);

            var fanouts = collaborators.db.Notifications.AsNoTracking()
                .Where(n => n.BroadcastId == broadcast.Id).ToList();
            fanouts.Should().HaveCount(1);
            fanouts[0].UserId.Should().Be(targetId);
        }
    }

    // Verifies the inactive-user rejection path: the handler explicitly
    // rejects broadcasts to inactive users (instead of silently fanning
    // out to zero users, which would leave a confusing "reached 0 users"
    // audit row). The test seeds one inactive user, targets them, and
    // verifies the handler returns the "BroadcastUserInactive" code.
    [Fact]
    public async Task SendBroadcastNotification_WithScopeUserOnInactiveUser_FailsWithBroadcastUserInactive()
    {
        // Arrange — seed one INACTIVE DomainUser.
        var collaborators = await BuildWiredCollaboratorsAsync();
        await using (collaborators.db)
        {
            await SeedActiveDomainUsersAsync(collaborators.db, activeCount: 0, inactiveCount: 1);
            var inactiveUserId = collaborators.db.DomainUsers
                .First(u => !u.IsActive).Id;

            var currentUser = new CurrentUserHelper(
                TestValues.CreatedByUserId, isAuthenticated: true,
                fullName: "Admin", groupId: null, roles: Roles.Admin);

            // Act
            var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
                new SendBroadcastNotificationCommand(
                    Title: "Should fail",
                    Message: "Target is inactive.",
                    Scope: BroadcastScope.User,
                    TargetRoleName: null,
                    TargetGroupId: null,
                    TargetUserId: inactiveUserId),
                currentUser,
                collaborators.userRepo,
                collaborators.groupRepo,
                collaborators.broadcastRepo,
                collaborators.notificationRepo,
                collaborators.preferenceRepo,
                collaborators.unitOfWork,
                collaborators.sendLogger,
                CancellationToken.None);

            // Assert — Failure with the stable error code "BroadcastUserInactive".
            // No broadcast row, no fanout row (atomic rollback contract).
            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Be("BroadcastUserInactive");

            collaborators.db.ChangeTracker.Clear();
            collaborators.db.BroadcastNotifications.AsNoTracking().ToList()
                .Should().BeEmpty();
            collaborators.db.Notifications.AsNoTracking().ToList()
                .Should().BeEmpty();
        }
    }
}

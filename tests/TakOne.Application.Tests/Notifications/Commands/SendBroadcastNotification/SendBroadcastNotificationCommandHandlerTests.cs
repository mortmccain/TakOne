using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Commands;
using TakOne.Application.Notifications.Commands.SendBroadcastNotification;
using TakOne.Application.Notifications.Errors;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Commands.SendBroadcastNotification;

/// <summary>
/// Unit tests for <see cref="SendBroadcastNotificationCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the user repository, the customer-
/// group repository, the broadcast-notification repository, the
/// notification repository, the unit of work, a logger, and a
/// cancellation token. We mock every collaborator with NSubstitute —
/// INCLUDING the static <see cref="BroadcastFanout"/> helper is not
/// directly mockable, so we let the real fanout code path execute
/// against mocked repositories + unit-of-work and assert on the mocks
/// the fanout uses internally.
///
/// SPECIAL CASES:
///   1. Defense-in-depth auth check: not-Authenticated OR UserId=Guid.Empty
///      OR not-in-role-Admin → Result&lt;int&gt;.Failure with the stable
///      code "BroadcastAuthRequired".
///   2. Scope=Group + TargetGroupId present → check group exists via
///      groupRepository.GetByIdReadOnlyAsync; null → "BroadcastGroupNotFound".
///   3. Scope=User + TargetUserId present → check user exists + IsActive;
///      null → "BroadcastUserNotFound"; inactive → "BroadcastUserInactive".
///   4. Scope=All → no extra checks, delegate straight to fanout.
///   5. Scope=Role → no extra DB checks (the validator already verified
///      TargetRoleName is in ValidRoleNames), delegate straight to fanout.
///
/// COVERAGE TARGETS: all 5 scope+target branches + admin-role check +
/// non-admin (authenticated) reject + unauthenticated reject.
/// </summary>
public class SendBroadcastNotificationCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // The BroadcastFanout.ExecuteAsync helper delegates to:
    //   - userRepository.GetAllActiveUserIdsAsync (Scope=All)
    //   - userRepository.GetActiveUserIdsInRoleAsync (Scope=Role)
    //   - userRepository.GetActiveUserIdsInGroupAsync (Scope=Group)
    //   - userRepository.GetByIdAsync (Scope=User)
    // Plus:
    //   - broadcastRepository.AddAsync (always — for the audit row)
    //   - notificationRepository.AddAsync (per recipient)
    //   - unitOfWork.SaveChangesAsync (always — atomic persistence)
    //   - logger.LogInformation (always — the fanout audit log)
    //
    // For the happy paths, we wire all the mocks to satisfy the fanout
    // call. For the rejection paths (group-not-found, user-not-found,
    // user-inactive), we short-circuit BEFORE the fanout is invoked —
    // the handler returns Failure early.
    private static (
        ICurrentUserService currentUser,
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        IBroadcastNotificationRepository broadcastRepo,
        INotificationRepository notificationRepo,
        INotificationPreferenceRepository preferenceRepo,
        IUnitOfWork unitOfWork,
        ILogger<SendBroadcastNotificationCommandHandler> logger)
        BuildMocks()
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);
        currentUser.IsInRole(Roles.Admin).Returns(true);

        var userRepo = Substitute.For<IUserRepository>();
        // Default: Scope=All returns 3 recipients.
        userRepo.GetAllActiveUserIdsAsync(default)
            .ReturnsForAnyArgs(new List<Guid>
            {
                TestValues.CreatedByUserId,
                TestValues.CustomerId,
                TestValues.UserId
            });
        // Scope=Role returns 2 (the customers).
        userRepo.GetActiveUserIdsInRoleAsync(string.Empty, default)
            .ReturnsForAnyArgs(new List<Guid>
            {
                TestValues.CustomerId,
                TestValues.UserId
            });
        // Scope=Group returns 1.
        userRepo.GetActiveUserIdsInGroupAsync(default, default)
            .ReturnsForAnyArgs(new List<Guid> { TestValues.CustomerId });
        // Scope=User returns 1 (the targeted user is found + active).
        userRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(User.CreateStaff("EMP-001", "Target User"));

        var groupRepo = Substitute.For<ICustomerGroupRepository>();
        // Default: the targeted group exists.
        groupRepo.GetByIdReadOnlyAsync(default, default)
            .ReturnsForAnyArgs(CustomerGroup.Create(
                "Sales Team",
                new Money(1_000_000m, TestValues.IRR)));

        var broadcastRepo = Substitute.For<IBroadcastNotificationRepository>();
        var notificationRepo = Substitute.For<INotificationRepository>();

        // Round 3 — notification preferences: default = nobody muted the
        // Broadcast kind (empty muted set), so the fanout reaches everyone.
        var preferenceRepo = Substitute.For<INotificationPreferenceRepository>();
        preferenceRepo.GetMutedUserIdsAsync(
                Arg.Any<TakOne.Domain.Notifications.Enums.NotificationKind>(),
                Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>() as IReadOnlySet<Guid>);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<SendBroadcastNotificationCommandHandler>>();

        return (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger);
    }

    // ── Scope=All happy path ──────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenScopeIsAllAndNoTargets_ReturnsSuccessWithRecipientCount()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        // Scope=All — all target fields are null per the validator's
        // Custom rule (the validator would reject this command if any
        // target was set).
        var command = new SendBroadcastNotificationCommand(
            Title: "Global announcement",
            Message: "Hello everyone",
            Scope: BroadcastScope.All,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // 3 recipients were configured for Scope=All in BuildMocks.
        result.Value.Should().Be(3);
    }

    // Scope=All happy path delegates STRAIGHT to the fanout — the
    // handler does NOT call groupRepository.GetByIdReadOnlyAsync (that
    // call only fires when Scope==Group). We assert the group repo is
    // NOT called to lock in the per-scope branch behavior (a refactor
    // that adds a Scope=All group existence check would fail this test).
    [Fact]
    public async Task HandleAsync_WhenScopeIsAll_DoesNotCallCheckGroupRepository()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        var command = new SendBroadcastNotificationCommand(
            Title: "Global announcement",
            Message: "Hello everyone",
            Scope: BroadcastScope.All,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await groupRepo.DidNotReceive().GetByIdReadOnlyAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // ── Scope=Role happy path ──────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenScopeIsRoleAndTargetRoleNameSet_ReturnsSuccessWithRecipientCount()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        var command = new SendBroadcastNotificationCommand(
            Title: "Customer-only sale",
            Message: "20% off this weekend",
            Scope: BroadcastScope.Role,
            TargetRoleName: Roles.Customer,
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // 2 recipients configured for Scope=Role.
        result.Value.Should().Be(2);
    }

    // ── Scope=Group happy path ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenScopeIsGroupAndGroupExists_ReturnsSuccessWithRecipientCount()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        var command = new SendBroadcastNotificationCommand(
            Title: "Group-targeted message",
            Message: "Hello group members",
            Scope: BroadcastScope.Group,
            TargetRoleName: null,
            TargetGroupId: TestValues.GroupId,
            TargetUserId: null);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // 1 recipient configured for Scope=Group.
        result.Value.Should().Be(1);
        // The handler MUST verify the group exists (defense-in-depth)
        // BEFORE delegating to the fanout.
        await groupRepo.Received(1).GetByIdReadOnlyAsync(
            Arg.Is<Guid>(g => g == TestValues.GroupId),
            Arg.Any<CancellationToken>());
    }

    // Scope=Group but the targeted group does NOT exist → handler returns
    // "BroadcastGroupNotFound" BEFORE delegating to fanout.
    [Fact]
    public async Task HandleAsync_WhenScopeIsGroupAndGroupDoesNotExist_ReturnsBroadcastGroupNotFound()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        groupRepo.GetByIdReadOnlyAsync(default, default)
            .ReturnsForAnyArgs((CustomerGroup?)null);
        var command = new SendBroadcastNotificationCommand(
            Title: "Group-targeted message",
            Message: "Hello group members",
            Scope: BroadcastScope.Group,
            TargetRoleName: null,
            TargetGroupId: TestValues.GroupId,
            TargetUserId: null);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(NotificationErrors.FormatBroadcastGroupNotFound());
        result.Error.Should().Be("BroadcastGroupNotFound");
        // The fanout must NOT be called when the group is missing — no
        // audit row should be created for a non-existent target.
        await broadcastRepo.DidNotReceive().AddAsync(
            Arg.Any<Domain.Notifications.Entities.BroadcastNotification>(),
            Arg.Any<CancellationToken>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Scope=User happy path ──────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenScopeIsUserAndUserExistsAndIsActive_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        var command = new SendBroadcastNotificationCommand(
            Title: "User-targeted message",
            Message: "Hello there",
            Scope: BroadcastScope.User,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: TestValues.UserId);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // The targeted user is found + active → 1 recipient.
        result.Value.Should().Be(1);
        // The handler calls GetByIdAsync TWICE for Scope=User:
        //   1. In the handler's own defense-in-depth check (verify the
        //      user exists + is active BEFORE delegating to the fanout).
        //   2. In the BroadcastFanout.ExecuteAsync helper's
        //      ResolveSingleUserAsync method (which checks again to
        //      resolve the recipient list — defensive against the user
        //      being deactivated between the handler's check and the
        //      fanout's resolution).
        await userRepo.Received(2).GetByIdAsync(
            Arg.Is<Guid>(u => u == TestValues.UserId),
            Arg.Any<CancellationToken>());
    }

    // Scope=User but the targeted user does NOT exist → handler returns
    // "BroadcastUserNotFound" BEFORE delegating to fanout.
    [Fact]
    public async Task HandleAsync_WhenScopeIsUserAndUserDoesNotExist_ReturnsBroadcastUserNotFound()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        userRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((User?)null);
        var command = new SendBroadcastNotificationCommand(
            Title: "User-targeted message",
            Message: "Hello there",
            Scope: BroadcastScope.User,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: TestValues.UserId);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(NotificationErrors.FormatBroadcastUserNotFound());
        result.Error.Should().Be("BroadcastUserNotFound");
        await broadcastRepo.DidNotReceive().AddAsync(
            Arg.Any<Domain.Notifications.Entities.BroadcastNotification>(),
            Arg.Any<CancellationToken>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // Scope=User but the targeted user is INACTIVE (soft-deactivated) →
    // handler returns "BroadcastUserInactive".
    [Fact]
    public async Task HandleAsync_WhenScopeIsUserAndUserIsInactive_ReturnsBroadcastUserInactive()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        // Build a user, then deactivate it.
        var targetUser = User.CreateStaff("EMP-001", "Target User");
        targetUser.Deactivate();
        userRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(targetUser);
        var command = new SendBroadcastNotificationCommand(
            Title: "User-targeted message",
            Message: "Hello there",
            Scope: BroadcastScope.User,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: TestValues.UserId);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(NotificationErrors.FormatBroadcastUserInactive());
        result.Error.Should().Be("BroadcastUserInactive");
        await broadcastRepo.DidNotReceive().AddAsync(
            Arg.Any<Domain.Notifications.Entities.BroadcastNotification>(),
            Arg.Any<CancellationToken>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Auth rejection ─────────────────────────────────────────────────

    // Not authenticated → BroadcastAuthRequired (defense-in-depth — the
    // [RequireRoles(Admin)] attribute should already reject this).
    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsBroadcastAuthRequired()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);
        var command = new SendBroadcastNotificationCommand(
            Title: "Hello",
            Message: "World",
            Scope: BroadcastScope.All,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(NotificationErrors.FormatBroadcastAuthRequired());
        result.Error.Should().Be("BroadcastAuthRequired");
        // Auth rejection short-circuits BEFORE any repo / fanout call.
        await broadcastRepo.DidNotReceive().AddAsync(
            Arg.Any<Domain.Notifications.Entities.BroadcastNotification>(),
            Arg.Any<CancellationToken>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // Authenticated but UserId=Guid.Empty → still BroadcastAuthRequired
    // (the second branch of the auth check catches the missing id).
    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsBroadcastAuthRequired()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);
        var command = new SendBroadcastNotificationCommand(
            Title: "Hello",
            Message: "World",
            Scope: BroadcastScope.All,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("BroadcastAuthRequired");
    }

    // Authenticated but NOT in Admin role → BroadcastAuthRequired
    // (defense-in-depth — the [RequireRoles(Admin)] attribute should
    // already reject this via Wolverine's middleware; the handler's
    // auth check is the second layer).
    [Fact]
    public async Task HandleAsync_WhenNotAdmin_ReturnsBroadcastAuthRequired()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        currentUser.IsInRole(Roles.Admin).Returns(false);
        // Even if they're in some other role, the admin check fails.
        currentUser.IsInRole(Roles.Manager).Returns(true);
        var command = new SendBroadcastNotificationCommand(
            Title: "Hello",
            Message: "World",
            Scope: BroadcastScope.All,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("BroadcastAuthRequired");
        await broadcastRepo.DidNotReceive().AddAsync(
            Arg.Any<Domain.Notifications.Entities.BroadcastNotification>(),
            Arg.Any<CancellationToken>());
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenScopeIsGroup_ForwardsCancellationTokenToGroupRepository()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var command = new SendBroadcastNotificationCommand(
            Title: "Group-targeted message",
            Message: "Hello group members",
            Scope: BroadcastScope.Group,
            TargetRoleName: null,
            TargetGroupId: TestValues.GroupId,
            TargetUserId: null);

        // Act
        await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger, ct);

        // Assert
        await groupRepo.Received(1).GetByIdReadOnlyAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenScopeIsUser_ForwardsCancellationTokenToUserRepository()
    {
        // Arrange
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var command = new SendBroadcastNotificationCommand(
            Title: "User-targeted message",
            Message: "Hello there",
            Scope: BroadcastScope.User,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: TestValues.UserId);

        // Act
        await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger, ct);

        // Assert
        // The handler calls GetByIdAsync TWICE for Scope=User (once in
        // its own check, once in the fanout's ResolveSingleUserAsync).
        // Both calls must forward the cancellation token.
        await userRepo.Received(2).GetByIdAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Mute suppression (Round 3 — notification preferences) ──────────

    // Users who muted the Broadcast kind are EXCLUDED from the fanout:
    // no Notification row, no SignalR ping. RecipientCount reflects the
    // POST-filter count ("reached N users"), and the muted users' Ids
    // never reach notificationRepository.AddAsync.
    [Fact]
    public async Task HandleAsync_WhenSomeRecipientsMutedBroadcastKind_SkipsThemAndReturnsFilteredCount()
    {
        // Arrange — BuildMocks wires 3 Scope=All recipients:
        // CreatedByUserId, CustomerId, UserId. Mute CustomerId.
        var (currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        preferenceRepo.GetMutedUserIdsAsync(
                Arg.Any<Domain.Notifications.Enums.NotificationKind>(),
                Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { TestValues.CustomerId } as IReadOnlySet<Guid>);
        var command = new SendBroadcastNotificationCommand(
            Title: "Global announcement",
            Message: "Hello everyone",
            Scope: BroadcastScope.All,
            TargetRoleName: null,
            TargetGroupId: null,
            TargetUserId: null);

        // Act
        var result = await SendBroadcastNotificationCommandHandler.HandleAsync(
            command, currentUser, userRepo, groupRepo, broadcastRepo, notificationRepo, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert — 3 resolved, 1 muted ⇒ 2 fanout rows + RecipientCount=2.
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(2);

        // The muted user's fanout row was never created…
        await notificationRepo.Received(2).AddAsync(
            Arg.Any<Domain.Notifications.Entities.Notification>(),
            Arg.Any<CancellationToken>());
        await notificationRepo.DidNotReceive().AddAsync(
            Arg.Is<Domain.Notifications.Entities.Notification>(n => n.UserId == TestValues.CustomerId),
            Arg.Any<CancellationToken>());

        // …and the audit row records the post-filter count (the
        // operationally meaningful "reached" number).
        await broadcastRepo.Received(1).AddAsync(
            Arg.Is<Domain.Notifications.Entities.BroadcastNotification>(b => b.RecipientCount == 2),
            Arg.Any<CancellationToken>());
    }
}

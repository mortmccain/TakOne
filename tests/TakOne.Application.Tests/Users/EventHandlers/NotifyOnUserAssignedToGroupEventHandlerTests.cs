using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Users.EventHandlers;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Users.Events;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Users.EventHandlers;

/// <summary>
/// Unit tests for <see cref="NotifyOnUserAssignedToGroupEventHandler"/>.
///
/// COVERAGE APPROACH:
///   The Round 2 feature closes a UX gap: reassigning a user's group
///   silently changes their salary budget, per-product purchase limits,
///   and salary currency. The handler creates ONE GroupChanged
///   notification for the affected user, with the new group's display
///   name in the Reason field (rendered by the UI as the context
///   sub-line).
///
///   Tests cover:
///     • genuine reassignment (different previous group) → notification
///       created with Kind=GroupChanged and Reason = new group name
///     • first-ever assignment (previous group null — a staff user
///       joining a group) → notification created (all constraints newly
///       apply)
///     • no-op reassignment (previous == new) → NO notification (pure
///       noise suppression)
///     • new group no longer resolvable (hard-delete race) → notification
///       still created, Reason null (degrades gracefully)
///     • CancellationToken forwarding to both repositories
///     • user muted the GroupChanged kind → NO notification (per-user
///       notification preferences, Round 3)
/// </summary>
public class NotifyOnUserAssignedToGroupEventHandlerTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    private static CustomerGroup BuildGroup(string name)
        => CustomerGroup.Create(name, new Money(10_000m, "IRR"));

    private static (
        ICustomerGroupRepository groupRepo,
        INotificationRepository notificationRepo,
        INotificationPreferenceRepository preferenceRepo,
        IUnitOfWork unitOfWork,
        ILogger<NotifyOnUserAssignedToGroupEventHandler> logger)
        BuildMocks(CustomerGroup? newGroup = null, bool groupChangedMuted = false)
    {
        var groupRepo = Substitute.For<ICustomerGroupRepository>();
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(newGroup ?? BuildGroup("VIP Customers"));

        var notificationRepo = Substitute.For<INotificationRepository>();

        var preferenceRepo = Substitute.For<INotificationPreferenceRepository>();
        preferenceRepo.IsMutedAsync(
                Arg.Any<Guid>(),
                TakOne.Domain.Notifications.Enums.NotificationKind.GroupChanged,
                Arg.Any<CancellationToken>())
            .Returns(groupChangedMuted);

        var unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<NotifyOnUserAssignedToGroupEventHandler>>();

        return (groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger);
    }

    // ── Genuine reassignment ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenReassignedToDifferentGroup_CreatesGroupChangedNotificationWithGroupName()
    {
        // Arrange — user moved from one group to another.
        var newGroup = BuildGroup("Wholesale Tier");
        var (groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks(newGroup);
        var @event = new UserAssignedToGroupDomainEvent(
            TestValues.UserId, TestValues.GroupId, newGroup.Id);

        // Act
        await NotifyOnUserAssignedToGroupEventHandler.HandleAsync(
            @event, groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        // Assert — one notification for the affected user, Kind GroupChanged,
        // Reason carrying the new group's display name.
        await notificationRepo.Received(1).AddAsync(
            Arg.Is<TakOne.Domain.Notifications.Entities.Notification>(n =>
                n.UserId == TestValues.UserId
                && n.Kind == TakOne.Domain.Notifications.Enums.NotificationKind.GroupChanged
                && n.Reason == "Wholesale Tier"),
            Arg.Any<CancellationToken>());
    }

    // ── First-ever assignment (staff joining a group) ─────────────────

    [Fact]
    public async Task HandleAsync_WhenFirstAssignment_CreatesNotification()
    {
        // Arrange — PreviousGroupId null: the user had no group (staff),
        // and now joins one — their budget/limits/currency constraints
        // all newly apply. This is a meaningful change worth notifying.
        var (groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        var @event = new UserAssignedToGroupDomainEvent(
            TestValues.UserId, null, TestValues.GroupId);

        // Act
        await NotifyOnUserAssignedToGroupEventHandler.HandleAsync(
            @event, groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).AddAsync(
            Arg.Any<TakOne.Domain.Notifications.Entities.Notification>(),
            Arg.Any<CancellationToken>());
    }

    // ── No-op suppression ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenSameGroupReassigned_DoesNotCreateNotification()
    {
        // Arrange — AssignToGroup raises the event unconditionally, even
        // for a same-group reassignment. The handler must suppress the
        // notification (nothing changed — noise otherwise).
        var (groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        var @event = new UserAssignedToGroupDomainEvent(
            TestValues.UserId, TestValues.GroupId, TestValues.GroupId);

        // Act
        await NotifyOnUserAssignedToGroupEventHandler.HandleAsync(
            @event, groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        // Assert
        await notificationRepo.DidNotReceiveWithAnyArgs().AddAsync(
            default(TakOne.Domain.Notifications.Entities.Notification)!, default);
    }

    // ── New group unresolvable (defensive) ────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNewGroupMissing_CreatesNotificationWithNullReason()
    {
        // Arrange — the group was hard-deleted between assignment and
        // notification (extremely unlikely; assignment requires an
        // existing group). The notification degrades gracefully: the
        // message itself is meaningful without the sub-line.
        var (groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks(newGroup: null);
        groupRepo.GetByIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((CustomerGroup?)null);
        var @event = new UserAssignedToGroupDomainEvent(
            TestValues.UserId, TestValues.GroupId, TestValues.GroupId2);

        // Act
        await NotifyOnUserAssignedToGroupEventHandler.HandleAsync(
            @event, groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).AddAsync(
            Arg.Is<TakOne.Domain.Notifications.Entities.Notification>(n =>
                n.Reason == null),
            Arg.Any<CancellationToken>());
    }

    // ── CancellationToken forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_ForwardsCancellationTokenToRepositories()
    {
        // Arrange
        var (groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger) = BuildMocks();
        var @event = new UserAssignedToGroupDomainEvent(
            TestValues.UserId, TestValues.GroupId, TestValues.GroupId2);
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await NotifyOnUserAssignedToGroupEventHandler.HandleAsync(
            @event, groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger, ct);

        // Assert
        await groupRepo.Received(1).GetByIdReadOnlyAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
        await notificationRepo.Received(1).AddAsync(
            Arg.Any<TakOne.Domain.Notifications.Entities.Notification>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Mute suppression (Round 3 — notification preferences) ────────

    [Fact]
    public async Task HandleAsync_WhenUserMutedGroupChangedKind_DoesNotCreateNotification()
    {
        // Arrange — the user muted the GroupChanged kind in Settings.
        // The handler must skip row creation entirely: no INSERT, no
        // NotificationCreatedDomainEvent, no SignalR ping. The sale-audit
        // trail is unaffected (mutes only silence the bell).
        var (groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger) =
            BuildMocks(groupChangedMuted: true);
        var @event = new UserAssignedToGroupDomainEvent(
            TestValues.UserId, TestValues.GroupId, TestValues.GroupId2);

        // Act
        await NotifyOnUserAssignedToGroupEventHandler.HandleAsync(
            @event, groupRepo, notificationRepo, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        // Assert
        await notificationRepo.DidNotReceiveWithAnyArgs().AddAsync(
            default(TakOne.Domain.Notifications.Entities.Notification)!, default);
        // The mute check must have consulted the preference repository
        // for the AFFECTED user (scope guard: preferences are per-user).
        await preferenceRepo.Received(1).IsMutedAsync(
            TestValues.UserId,
            TakOne.Domain.Notifications.Enums.NotificationKind.GroupChanged,
            Arg.Any<CancellationToken>());
    }
}

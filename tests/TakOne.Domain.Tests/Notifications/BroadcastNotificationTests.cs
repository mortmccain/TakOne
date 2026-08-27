using FluentAssertions;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Domain.Tests.Notifications;

/// <summary>
/// Unit tests for the <see cref="BroadcastNotification"/> aggregate root —
/// the admin-authored audit row for a fanout broadcast.
///
/// Focuses on the rich scope-target consistency invariants
/// (each Scope value dictates exactly which Target* fields may be non-null),
/// the Title/Message bounds, the FanoutKind guard
/// (only Broadcast / AppUpdate allowed), the RecipientCount non-negative
/// rule, and the deliberate absence of a domain event on Create().
/// </summary>
public class BroadcastNotificationTests
{
    // Helper: build a Broadcast with scope=All (the simplest valid shape).
    private static BroadcastNotification BuildAllBroadcast() =>
        BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "System Update",
            message: "The app will be down for maintenance.",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 42);

    // ======================================================================
    //                          Create — HAPPY PATH (All)
    // ======================================================================

    [Fact]
    public void Create_ForScopeAllWithAllTargetsNull_ReturnsBroadcastWithCorrectFields()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act
        var broadcast = BuildAllBroadcast();

        // Assert
        broadcast.Id.Should().NotBeEmpty();
        broadcast.SentByUserId.Should().Be(TestValues.UserId);
        broadcast.Scope.Should().Be(BroadcastScope.All);
        broadcast.TargetRoleName.Should().BeNull();
        broadcast.TargetGroupId.Should().BeNull();
        broadcast.TargetUserId.Should().BeNull();
        broadcast.Title.Should().Be("System Update");
        broadcast.Message.Should().Be("The app will be down for maintenance.");
        broadcast.FanoutKind.Should().Be(NotificationKind.Broadcast);
        broadcast.RecipientCount.Should().Be(42);
        broadcast.SentAtUtc.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_ForScopeAll_DoesNotRaiseDomainEvent()
    {
        // Act — BroadcastNotification deliberately doesn't raise on Create()
        var broadcast = BuildAllBroadcast();

        // Assert — the per-user Notification fanout rows each raise their own
        // NotificationCreatedDomainEvent; a separate BroadcastCreatedDomainEvent
        // would be redundant and add a no-subscriber warning to Wolverine logs.
        broadcast.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Create_WithSentByUserIdEmpty_IsAllowedForSystemEmittedBroadcasts()
    {
        // Act — AppUpdate broadcasts are sent on behalf of the system (no human author)
        var broadcast = BroadcastNotification.Create(
            sentByUserId: Guid.Empty,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "App updated",
            message: "Please reload",
            fanoutKind: NotificationKind.AppUpdate,
            recipientCount: 0);

        // Assert — empty SentByUserId is explicitly allowed by design
        broadcast.SentByUserId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Create_WithRecipientCountZero_IsAllowedForEmptyGroups()
    {
        // Act — admins can broadcast to a deactivated group with no members
        var broadcast = BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Group,
            targetRoleName: null,
            targetGroupId: TestValues.GroupId,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        // Assert
        broadcast.RecipientCount.Should().Be(0);
    }

    // ======================================================================
    //                          Scope=All — guards
    // ======================================================================

    [Fact]
    public void Create_ForScopeAllWithNonEmptyTargetRoleName_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: "Customer",
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=All must have null TargetRoleName, TargetGroupId, and TargetUserId.");
    }

    [Fact]
    public void Create_ForScopeAllWithNonNullTargetGroupId_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: TestValues.GroupId,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=All must have null TargetRoleName, TargetGroupId, and TargetUserId.");
    }

    [Fact]
    public void Create_ForScopeAllWithNonNullTargetUserId_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: TestValues.UserId,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=All must have null TargetRoleName, TargetGroupId, and TargetUserId.");
    }

    // ======================================================================
    //                          Scope=Role — happy path + guards
    // ======================================================================

    [Fact]
    public void Create_ForScopeRoleWithValidRoleName_Succeeds()
    {
        // Act
        var broadcast = BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Role,
            targetRoleName: "Employee",
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 5);

        // Assert
        broadcast.Scope.Should().Be(BroadcastScope.Role);
        broadcast.TargetRoleName.Should().Be("Employee");
    }

    [Fact]
    public void Create_ForScopeRoleWithEmptyTargetRoleName_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Role,
            targetRoleName: "",
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=Role requires a non-empty TargetRoleName.");
    }

    [Fact]
    public void Create_ForScopeRoleWithWhitespaceTargetRoleName_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Role,
            targetRoleName: "   ",
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=Role requires a non-empty TargetRoleName.");
    }

    [Fact]
    public void Create_ForScopeRoleWithNonNullTargetGroupId_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Role,
            targetRoleName: "Employee",
            targetGroupId: TestValues.GroupId,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=Role must have null TargetGroupId and TargetUserId.");
    }

    [Fact]
    public void Create_ForScopeRoleWithNonNullTargetUserId_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Role,
            targetRoleName: "Employee",
            targetGroupId: null,
            targetUserId: TestValues.UserId,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=Role must have null TargetGroupId and TargetUserId.");
    }

    // ======================================================================
    //                          Scope=Group — happy path + guards
    // ======================================================================

    [Fact]
    public void Create_ForScopeGroupWithValidGroupId_Succeeds()
    {
        // Act
        var broadcast = BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Group,
            targetRoleName: null,
            targetGroupId: TestValues.GroupId,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 7);

        // Assert
        broadcast.Scope.Should().Be(BroadcastScope.Group);
        broadcast.TargetGroupId.Should().Be(TestValues.GroupId);
    }

    [Fact]
    public void Create_ForScopeGroupWithEmptyTargetGroupId_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Group,
            targetRoleName: null,
            targetGroupId: Guid.Empty,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=Group requires a non-empty TargetGroupId.");
    }

    [Fact]
    public void Create_ForScopeGroupWithNonNullTargetRoleName_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Group,
            targetRoleName: "Employee",
            targetGroupId: TestValues.GroupId,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=Group must have null TargetRoleName and TargetUserId.");
    }

    [Fact]
    public void Create_ForScopeGroupWithNonNullTargetUserId_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.Group,
            targetRoleName: null,
            targetGroupId: TestValues.GroupId,
            targetUserId: TestValues.UserId,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=Group must have null TargetRoleName and TargetUserId.");
    }

    // ======================================================================
    //                          Scope=User — happy path + guards
    // ======================================================================

    [Fact]
    public void Create_ForScopeUserWithValidUserId_Succeeds()
    {
        // Act
        var broadcast = BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.User,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: TestValues.CustomerId,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 1);

        // Assert
        broadcast.Scope.Should().Be(BroadcastScope.User);
        broadcast.TargetUserId.Should().Be(TestValues.CustomerId);
    }

    [Fact]
    public void Create_ForScopeUserWithEmptyTargetUserId_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.User,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: Guid.Empty,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=User requires a non-empty TargetUserId.");
    }

    [Fact]
    public void Create_ForScopeUserWithNonNullTargetRoleName_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.User,
            targetRoleName: "Employee",
            targetGroupId: null,
            targetUserId: TestValues.UserId,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=User must have null TargetRoleName and TargetGroupId.");
    }

    [Fact]
    public void Create_ForScopeUserWithNonNullTargetGroupId_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.User,
            targetRoleName: null,
            targetGroupId: TestValues.GroupId,
            targetUserId: TestValues.UserId,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("Scope=User must have null TargetRoleName and TargetGroupId.");
    }

    // ======================================================================
    //                          Title / Message bounds
    // ======================================================================

    [Fact]
    public void Create_WithEmptyTitle_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>().WithMessage("Broadcast Title is required.");
    }

    [Fact]
    public void Create_WithWhitespaceTitle_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "   ",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>().WithMessage("Broadcast Title is required.");
    }

    [Fact]
    public void Create_WithTitleExceeding200Chars_Throws()
    {
        // Arrange — Title length 201 (after trim) → violates the 200-char cap
        var longTitle = new string('t', 201);

        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: longTitle,
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage($"Broadcast Title must be 200 characters or fewer (got 201).");
    }

    [Fact]
    public void Create_WithEmptyMessage_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>().WithMessage("Broadcast Message is required.");
    }

    [Fact]
    public void Create_WithWhitespaceMessage_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "   ",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>().WithMessage("Broadcast Message is required.");
    }

    [Fact]
    public void Create_WithMessageExceeding1000Chars_Throws()
    {
        // Arrange — Message length 1001 (after trim) → violates the 1000-char cap
        var longMessage = new string('m', 1001);

        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: longMessage,
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage($"Broadcast Message must be 1000 characters or fewer (got 1001).");
    }

    // ======================================================================
    //                          FanoutKind guard
    // ======================================================================

    [Fact]
    public void Create_WithFanoutKindSaleSubmitted_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.SaleSubmitted,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("FanoutKind must be Broadcast or AppUpdate (got SaleSubmitted). *");
    }

    [Fact]
    public void Create_WithFanoutKindSaleApproved_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.SaleApproved,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("FanoutKind must be Broadcast or AppUpdate (got SaleApproved). *");
    }

    // ======================================================================
    //                          RecipientCount guard
    // ======================================================================

    [Fact]
    public void Create_WithNegativeRecipientCount_Throws()
    {
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: -1);

        act.Should().Throw<DomainException>()
            .WithMessage("RecipientCount cannot be negative (got -1).");
    }

    // ======================================================================
    //                          Scope guard (undefined / zero)
    // ======================================================================

    [Fact]
    public void Create_WithUninitializedScope_ThrowsMustBeOneOfMessage()
    {
        // Arrange — default(BroadcastScope) = 0 is invalid (enum starts at 1).
        // Implementation note: the SUT's EnsureScopeValid has TWO guards:
        //   1) Enum.IsDefined check → "BroadcastScope must be one of: ..."
        //   2) explicit zero-check → "BroadcastScope cannot be 0 (Uninitialized). ..."
        // Guard #1 fires first for scope=0 because BroadcastScope starts at 1
        // (so Enum.IsDefined(typeof(BroadcastScope), 0) returns false), making
        // guard #2 effectively unreachable dead-code. We assert the actual
        // SUT behavior: the "must be one of" message is what's thrown.
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: 0,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        // Assert — actual behavior is the first guard firing
        act.Should().Throw<DomainException>()
            .WithMessage("BroadcastScope must be one of: All, Role, Group, User.");
    }

    [Fact]
    public void Create_WithUndefinedScope_Throws()
    {
        // Arrange — (BroadcastScope)999 is not a defined enum value
        Action act = () => BroadcastNotification.Create(
            sentByUserId: TestValues.UserId,
            scope: (BroadcastScope)999,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "T",
            message: "M",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 0);

        act.Should().Throw<DomainException>()
            .WithMessage("BroadcastScope must be one of: All, Role, Group, User.");
    }
}

using FluentAssertions;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Domain.Notifications.Events;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Domain.Tests.Notifications;

/// <summary>
/// Unit tests for the <see cref="Notification"/> aggregate root.
/// Verifies the sale-lifecycle <see cref="Notification.Create"/> factory
/// (raises NotificationCreatedDomainEvent), the
/// <see cref="Notification.CreateBroadcast"/> factory with its kind-guard
/// (only Broadcast / AppUpdate allowed), and MarkAsRead idempotency.
/// </summary>
public class NotificationTests
{
    // ======================================================================
    //                          Create — happy path
    // ======================================================================

    [Fact]
    public void Create_WithValidArgs_ReturnsNotificationWithCorrectProperties()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act
        var notification = Notification.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleSubmitted,
            saleId: TestValues.SaleId,
            saleDisplayNumber: "INT-۱۴۰۳-۰۰۰۰۰۰۴۲",
            actorName: "Staff Alice",
            reason: null);

        // Assert
        notification.Id.Should().NotBeEmpty();
        notification.UserId.Should().Be(TestValues.UserId);
        notification.Kind.Should().Be(NotificationKind.SaleSubmitted);
        notification.SaleId.Should().Be(TestValues.SaleId);
        notification.SaleDisplayNumber.Should().Be("INT-۱۴۰۳-۰۰۰۰۰۰۴۲");
        notification.ActorName.Should().Be("Staff Alice");
        notification.Reason.Should().BeNull();
        // Structured-only payload for sale lifecycle:
        notification.Title.Should().BeNull();
        notification.Message.Should().BeNull();
        notification.BroadcastId.Should().BeNull();
        notification.ReadAtUtc.Should().BeNull();
        notification.CreatedAtUtc.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_WithReason_SetsReasonField()
    {
        // Act — SaleCancelled notifications carry the cancellation reason
        var notification = Notification.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleCancelled,
            saleId: TestValues.SaleId,
            saleDisplayNumber: "INT-۱۴۰۳-۰۰۰۰۰۰۴۲",
            actorName: "Staff Alice",
            reason: "Customer changed mind");

        // Assert
        notification.Reason.Should().Be("Customer changed mind");
    }

    [Fact]
    public void Create_RaisesNotificationCreatedDomainEvent()
    {
        // Act
        var notification = Notification.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleApproved,
            saleId: TestValues.SaleId,
            saleDisplayNumber: "INT-۱۴۰۳-۰۰۰۰۰۰۴۲",
            actorName: "Alice",
            reason: null);

        // Assert
        notification.DomainEvents.Should().ContainSingle(e => e is NotificationCreatedDomainEvent);
        var ev = notification.DomainEvents.OfType<NotificationCreatedDomainEvent>().Single();
        ev.NotificationId.Should().Be(notification.Id);
        ev.UserId.Should().Be(notification.UserId);
        ev.Kind.Should().Be((int)NotificationKind.SaleApproved);
        ev.SaleDisplayNumber.Should().Be(notification.SaleDisplayNumber);
    }

    // ======================================================================
    //                          Create — guards
    // ======================================================================

    [Fact]
    public void Create_WithEmptyUserId_Throws()
    {
        Action act = () => Notification.Create(
            userId: Guid.Empty,
            kind: NotificationKind.SaleSubmitted,
            saleId: TestValues.SaleId,
            saleDisplayNumber: null,
            actorName: null);

        act.Should().Throw<DomainException>()
            .WithMessage("A notification must be targeted at a non-empty user Id.");
    }

    // ======================================================================
    //                          Create — nullable fields
    // ======================================================================

    [Fact]
    public void Create_WithNullSaleId_IsAllowedForFutureNonSaleNotifications()
    {
        // Act — future non-sale notifications have null SaleId
        var notification = Notification.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleSubmitted,
            saleId: null,
            saleDisplayNumber: null,
            actorName: null);

        // Assert
        notification.SaleId.Should().BeNull();
    }

    [Fact]
    public void Create_WithNullActorName_IsAllowedForCustomerRecipientPath()
    {
        // Act — customer is themselves the actor; null actorName is fine
        var notification = Notification.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleSubmitted,
            saleId: TestValues.SaleId,
            saleDisplayNumber: "INT-۱۴۰۳-۰۰۰۰۰۰۴۲",
            actorName: null);

        // Assert
        notification.ActorName.Should().BeNull();
    }

    // ======================================================================
    //                          CreateBroadcast — happy path
    // ======================================================================

    [Fact]
    public void CreateBroadcast_WithKindBroadcast_SetsAllFanoutFields()
    {
        // Act
        var notification = Notification.CreateBroadcast(
            userId: TestValues.UserId,
            kind: NotificationKind.Broadcast,
            broadcastId: TestValues.BroadcastId,
            title: "Announcement",
            message: "Hello everyone");

        // Assert
        notification.UserId.Should().Be(TestValues.UserId);
        notification.Kind.Should().Be(NotificationKind.Broadcast);
        notification.BroadcastId.Should().Be(TestValues.BroadcastId);
        notification.Title.Should().Be("Announcement");
        notification.Message.Should().Be("Hello everyone");
        // Sale-lifecycle structured fields are null for broadcasts:
        notification.SaleId.Should().BeNull();
        notification.SaleDisplayNumber.Should().BeNull();
        notification.ActorName.Should().BeNull();
        notification.Reason.Should().BeNull();
    }

    [Fact]
    public void CreateBroadcast_WithKindAppUpdate_Works()
    {
        // Act — AppUpdate fanout rows are emitted by the hosted service
        var notification = Notification.CreateBroadcast(
            userId: TestValues.UserId,
            kind: NotificationKind.AppUpdate,
            broadcastId: TestValues.BroadcastId,
            title: "New version",
            message: "Please reload");

        // Assert
        notification.Kind.Should().Be(NotificationKind.AppUpdate);
    }

    [Fact]
    public void CreateBroadcast_RaisesNotificationCreatedDomainEvent()
    {
        // Act
        var notification = Notification.CreateBroadcast(
            userId: TestValues.UserId,
            kind: NotificationKind.Broadcast,
            broadcastId: TestValues.BroadcastId,
            title: "T",
            message: "M");

        // Assert
        notification.DomainEvents.OfType<NotificationCreatedDomainEvent>().Should().ContainSingle();
    }

    // ======================================================================
    //                          CreateBroadcast — guards
    // ======================================================================

    [Fact]
    public void CreateBroadcast_WithKindSaleSubmitted_Throws()
    {
        Action act = () => Notification.CreateBroadcast(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleSubmitted,
            broadcastId: TestValues.BroadcastId,
            title: "T",
            message: "M");

        act.Should().Throw<DomainException>()
            .WithMessage("CreateBroadcast requires NotificationKind.Broadcast or AppUpdate (got SaleSubmitted). *");
    }

    [Fact]
    public void CreateBroadcast_WithKindSaleApproved_Throws()
    {
        Action act = () => Notification.CreateBroadcast(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleApproved,
            broadcastId: TestValues.BroadcastId,
            title: "T",
            message: "M");

        act.Should().Throw<DomainException>()
            .WithMessage("CreateBroadcast requires NotificationKind.Broadcast or AppUpdate (got SaleApproved). *");
    }

    [Fact]
    public void CreateBroadcast_WithKindSaleInvoiced_Throws()
    {
        Action act = () => Notification.CreateBroadcast(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleInvoiced,
            broadcastId: TestValues.BroadcastId,
            title: "T",
            message: "M");

        act.Should().Throw<DomainException>()
            .WithMessage("CreateBroadcast requires NotificationKind.Broadcast or AppUpdate (got SaleInvoiced). *");
    }

    [Fact]
    public void CreateBroadcast_WithKindSaleCancelled_Throws()
    {
        Action act = () => Notification.CreateBroadcast(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleCancelled,
            broadcastId: TestValues.BroadcastId,
            title: "T",
            message: "M");

        act.Should().Throw<DomainException>()
            .WithMessage("CreateBroadcast requires NotificationKind.Broadcast or AppUpdate (got SaleCancelled). *");
    }

    [Fact]
    public void CreateBroadcast_WithEmptyBroadcastId_Throws()
    {
        Action act = () => Notification.CreateBroadcast(
            userId: TestValues.UserId,
            kind: NotificationKind.Broadcast,
            broadcastId: Guid.Empty,
            title: "T",
            message: "M");

        act.Should().Throw<DomainException>()
            .WithMessage("A broadcast fanout Notification must reference a non-empty BroadcastId.");
    }

    // ======================================================================
    //                          CreateBroadcast — nullable fields
    // ======================================================================

    [Fact]
    public void CreateBroadcast_WithTitleNull_IsAllowed()
    {
        // Act — title/message are nullable strings; the factory doesn't
        // validate them (the parent BroadcastNotification aggregate does
        // its own bounds check)
        var notification = Notification.CreateBroadcast(
            userId: TestValues.UserId,
            kind: NotificationKind.Broadcast,
            broadcastId: TestValues.BroadcastId,
            title: null,
            message: null);

        // Assert
        notification.Title.Should().BeNull();
        notification.Message.Should().BeNull();
    }

    // ======================================================================
    //                          MarkAsRead
    // ======================================================================

    [Fact]
    public void MarkAsRead_FromUnread_SetsReadAtUtcCloseToNow()
    {
        // Arrange — unread notification
        var notification = Notification.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleSubmitted,
            saleId: TestValues.SaleId,
            saleDisplayNumber: null,
            actorName: null);
        var before = DateTime.UtcNow;

        // Act
        notification.MarkAsRead();

        // Assert
        notification.ReadAtUtc.Should().NotBeNull();
        notification.ReadAtUtc!.Value.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MarkAsRead_WhenAlreadyRead_IsIdempotentNoOp()
    {
        // Arrange — mark as read first, capture the timestamp
        var notification = Notification.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleSubmitted,
            saleId: TestValues.SaleId,
            saleDisplayNumber: null,
            actorName: null);
        notification.MarkAsRead();
        var firstReadAt = notification.ReadAtUtc!.Value;

        // Act — call MarkAsRead again
        notification.MarkAsRead();

        // Assert — ReadAtUtc unchanged; second call is a no-op
        notification.ReadAtUtc.Should().Be(firstReadAt);
    }

    [Fact]
    public void MarkAsRead_TwoConsecutiveCalls_DoNotChangeReadAtAfterFirst()
    {
        // Arrange
        var notification = Notification.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleSubmitted,
            saleId: TestValues.SaleId,
            saleDisplayNumber: null,
            actorName: null);

        // Act
        notification.MarkAsRead();
        var firstReadAt = notification.ReadAtUtc;
        // Sleep briefly to ensure DateTime.UtcNow would tick forward if not
        // for the idempotency guard.
        Thread.Sleep(10);
        notification.MarkAsRead();

        // Assert
        notification.ReadAtUtc.Should().Be(firstReadAt);
    }
}

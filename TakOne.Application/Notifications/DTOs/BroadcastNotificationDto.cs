using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Notifications.DTOs;

/// <summary>
/// Read-side DTO for a <see cref="Domain.Notifications.Entities.BroadcastNotification"/>
/// (the admin's audit-record view of a broadcast).
/// </summary>
/// <remarks>
/// Used by the admin audit list on the new <c>/Admin/Notifications</c>
/// page. Surfaces the broadcast's metadata (who sent, when, scope, target,
/// title, message, recipient count) so the admin can audit past broadcasts.
/// </remarks>
public sealed class BroadcastNotificationDto
{
    public Guid Id { get; init; }

    /// <summary>
    /// The user Id of the admin who authored the broadcast. <see cref="Guid.Empty"/>
    /// for system-emitted <see cref="NotificationKind.AppUpdate"/> broadcasts.
    /// </summary>
    public Guid SentByUserId { get; init; }

    /// <summary>
    /// The admin's display name (resolved by the handler via
    /// <c>IUserRepository.GetByIdAsync</c>). Null for system-emitted broadcasts
    /// (SentByUserId == Guid.Empty).
    /// </summary>
    public string? SentByUserName { get; init; }

    /// <summary>
    /// UTC timestamp the broadcast was sent. UI converts to local time +
    /// culture-relative format.
    /// </summary>
    public DateTimeOffset SentAtUtc { get; init; }

    /// <summary>
    /// The audience selector. UI maps each value to a localized label
    /// ("Everyone", "Role: Employee", "Group: Sales Team",
    /// "User: Sara Ahmadi").
    /// </summary>
    public BroadcastScope Scope { get; init; }

    /// <summary>
    /// Set when <see cref="Scope"/> == <see cref="BroadcastScope.Role"/>.
    /// The ASP.NET Identity role name.
    /// </summary>
    public string? TargetRoleName { get; init; }

    /// <summary>
    /// Set when <see cref="Scope"/> == <see cref="BroadcastScope.Group"/>.
    /// The customer group's Id.
    /// </summary>
    public Guid? TargetGroupId { get; init; }

    /// <summary>
    /// The customer group's name (resolved by the handler via
    /// <c>ICustomerGroupRepository.GetByIdReadOnlyAsync</c>). Null for
    /// non-group scopes or if the group was deleted after the broadcast.
    /// </summary>
    public string? TargetGroupName { get; init; }

    /// <summary>
    /// Set when <see cref="Scope"/> == <see cref="BroadcastScope.User"/>.
    /// The recipient user's Id.
    /// </summary>
    public Guid? TargetUserId { get; init; }

    /// <summary>
    /// The recipient user's display name (resolved by the handler via
    /// <c>IUserRepository.GetByIdAsync</c>). Null for non-user scopes or if
    /// the user was deleted after the broadcast.
    /// </summary>
    public string? TargetUserName { get; init; }

    /// <summary>
    /// The admin-authored subject line.
    /// </summary>
    public string Title { get; init; } = null!;

    /// <summary>
    /// The admin-authored message body.
    /// </summary>
    public string Message { get; init; } = null!;

    /// <summary>
    /// The <see cref="NotificationKind"/> of the fanout rows — Broadcast for
    /// admin-authored, AppUpdate for the auto-emitted app-update broadcast.
    /// Lets the admin audit list filter "only system broadcasts" vs
    /// "only admin-authored".
    /// </summary>
    public NotificationKind FanoutKind { get; init; }

    /// <summary>
    /// How many per-user <see cref="Domain.Notifications.Entities.Notification"/>
    /// rows were created in this broadcast. The admin sees "reached N users"
    /// in the audit list.
    /// </summary>
    public int RecipientCount { get; init; }
}

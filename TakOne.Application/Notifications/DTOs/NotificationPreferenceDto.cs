using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Notifications.DTOs;

/// <summary>
/// One row of the Settings page's notification-preferences card: the kind
/// (icon + localized label rendered by the UI) and whether the current
/// user has muted it.
/// </summary>
/// <remarks>
/// <para>
/// <b>COMPLETE LIST GUARANTEE</b>: <c>GetNotificationPreferencesQueryHandler</c>
/// emits ONE DTO PER <see cref="NotificationKind"/> enum value — kinds with
/// no persisted preference row are returned with
/// <see cref="IsMuted"/> = false (the sparse default). The settings UI can
/// therefore render the full kind list from the query result without
/// duplicating the enum's values on the client.
/// </para>
/// <para>
/// <b>NO PERSISTED TEXT</b>: the DTO carries only the enum — the UI
/// localizes the kind's display name at render time (same strategy as
/// <see cref="NotificationDto"/>, see its LOCALIZATION STRATEGY remarks).
/// </para>
/// </remarks>
public sealed class NotificationPreferenceDto
{
    /// <summary>
    /// The notification kind this row describes.
    /// </summary>
    public NotificationKind Kind { get; init; }

    /// <summary>
    /// True = the current user has muted this kind (notification rows of
    /// this kind are suppressed at creation time). False = normal
    /// delivery.
    /// </summary>
    public bool IsMuted { get; init; }
}

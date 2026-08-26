using TakOne.Domain.Notifications.Enums;

namespace TakOne.Application.Notifications.DTOs;

/// <summary>
/// Read-side DTO for a <see cref="Domain.Notifications.Entities.Notification"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>STRUCTURED-ONLY DESIGN</b>: the DTO exposes the discriminator
/// (<see cref="Kind"/>) and the structured params (<see cref="SaleDisplayNumber"/>,
/// <see cref="ActorName"/>, <see cref="Reason"/>). The UI layer does the
/// localization at render time by looking up the resx template for the
/// kind and formatting it with the params — the DB stores no localized
/// text. This means language switches work seamlessly for historical
/// notifications (a Persian user who switches to English sees their old
/// notifications in English, not stuck in Persian).
/// </para>
/// <para>
/// <b>KIND is exposed as the enum (not the int)</b>: the UI's switch is
/// type-safe. Persisted as int in the DB; the DTO surfaces the enum.
/// </para>
/// </remarks>
public sealed class NotificationDto
{
    public Guid Id { get; init; }

    /// <summary>
    /// The kind of activity this notification represents. UI uses this
    /// to pick an icon (e.g. <c>ShoppingCart</c> for Submitted,
    /// <c>CheckCircle</c> for Approved, <c>LocalShipping</c> for
    /// Invoiced, <c>Cancel</c> for Cancelled).
    /// </summary>
    public NotificationKind Kind { get; init; }

    /// <summary>
    /// The Id of the sale this notification is about. Used for the
    /// notification's "view order" deep-link.
    /// </summary>
    public Guid? SaleId { get; init; }

    /// <summary>
    /// Display-safe sale identifier snapshot (e.g. "INT-1505-00000042").
    /// </summary>
    public string? SaleDisplayNumber { get; init; }

    /// <summary>
    /// The full name of the staff member who acted on the sale
    /// (approver / invoicer / canceller). Null when the actor is the
    /// user themselves (self-buy SaleSubmitted — no point naming
    /// yourself).
    /// </summary>
    public string? ActorName { get; init; }

    /// <summary>
    /// The cancellation reason — only set for
    /// <see cref="NotificationKind.SaleCancelled"/>.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// UTC timestamp the notification was created. UI converts to local
    /// time + culture-relative format ("2 minutes ago").
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>
    /// UTC timestamp the user dismissed this notification. Null = unread.
    /// </summary>
    public DateTimeOffset? ReadAtUtc { get; init; }

    /// <summary>
    /// True when <see cref="ReadAtUtc"/> is null. Convenience for the UI
    /// so it doesn't have to do the null check.
    /// </summary>
    public bool IsUnread => ReadAtUtc is null;
}

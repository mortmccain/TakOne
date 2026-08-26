using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Notifications.Commands.MarkNotificationAsRead;

/// <summary>
/// Marks a single notification as read (sets <c>ReadAtUtc</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE INVARIANT</b>: the handler verifies the notification's
/// <c>UserId == currentUser.UserId</c> BEFORE marking it read. A caller
/// cannot mark someone else's notification as read — anti-CSRF.
/// </para>
/// <para>
/// <b>IDEMPOTENCY</b>: marking an already-read notification as read is a
/// no-op (the domain's <c>MarkAsRead()</c> is idempotent — see the
/// <c>Notification</c> aggregate). The UI's "tap to read" action can fire
/// multiple times safely (rapid clicks, race conditions, etc.).
/// </para>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireAuthentication]</c> — any authenticated
/// user can mark notifications as read (only their own).
/// </para>
/// </remarks>
[RequireAuthentication]
public sealed record MarkNotificationAsReadCommand(Guid NotificationId);

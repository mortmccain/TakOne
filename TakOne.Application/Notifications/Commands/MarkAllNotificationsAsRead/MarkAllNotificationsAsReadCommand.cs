using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Notifications.Commands.MarkAllNotificationsAsRead;

/// <summary>
/// Marks ALL of the current user's unread notifications as read in a single
/// UPDATE. Returns the number of rows affected (for the UI's toast:
/// "Marked 3 notifications as read").
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE INVARIANT</b>: the handler resolves <c>userId</c> from
/// <c>ICurrentUserService</c> and only marks notifications belonging to
/// that user. Anti-CSRF: the caller cannot supply a different user Id.
/// </para>
/// <para>
/// <b>RETURN VALUE</b>: <c>Result&lt;int&gt;</c> — the count of rows
/// affected. The UI can show "Marked N notifications as read" using this.
/// </para>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireAuthentication]</c> — any authenticated
/// user can mark their own notifications as read.
/// </para>
/// </remarks>
[RequireAuthentication]
public sealed record MarkAllNotificationsAsReadCommand;

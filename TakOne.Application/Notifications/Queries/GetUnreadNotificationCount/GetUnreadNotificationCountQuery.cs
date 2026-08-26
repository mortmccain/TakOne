using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Notifications.Queries.GetUnreadNotificationCount;

/// <summary>
/// Returns the count of the current user's UNREAD notifications
/// (ReadAtUtc is null). Used by the desktop top-bar bell icon badge
/// and the mobile header's bell badge — both polled on page load and
/// refreshed in real time via SignalR <c>ReceiveRefresh</c>.
/// </summary>
/// <remarks>
/// <para>
/// Returns an <c>int</c> directly (not wrapped in <c>Result&lt;&gt;</c>)
/// because the count is a primitive; auth failure returns 0 (silent —
/// the UI simply shows no badge, matching the empty-page pattern in
/// <c>GetNotificationsForUserQuery</c>).
/// </para>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireAuthentication]</c> — any authenticated
/// user can count their own unread notifications.
/// </para>
/// </remarks>
[RequireAuthentication]
public sealed class GetUnreadNotificationCountQuery
{
    // Intentionally parameterless — the handler resolves the user from
    // ICurrentUserService (the query is a pure "give me my count" call).
}

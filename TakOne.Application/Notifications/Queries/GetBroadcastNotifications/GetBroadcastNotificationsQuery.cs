using TakOne.Application.Common.Authorization;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Queries.GetBroadcastNotifications;

/// <summary>
/// Paginated list query for the admin's broadcast audit page
/// (<c>/Admin/Notifications</c>). Returns past broadcasts newest-first,
/// with the sender's name + scope-target display name resolved by the
/// handler.
/// </summary>
/// <remarks>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireRoles(Admin)]</c> — only Admin-role
/// users may see the audit list. A non-admin calling this gets an empty
/// page (silent — same pattern as <c>GetNotificationsForUserQuery</c>'s
/// auth-failure → empty page behavior).
/// </para>
/// <para>
/// <b>RETURN TYPE</b>: bare <see cref="PaginatedResult{T}"/> (NOT
/// wrapped in <c>Result&lt;&gt;</c>) — matches
/// <c>GetNotificationsForUserQuery</c> and <c>GetSalesPaginatedQuery</c>
/// so the UI can call
/// <c>InvokeAsync&lt;PaginatedResult&lt;BroadcastNotificationDto&gt;&gt;</c>.
/// </para>
/// </remarks>
[RequireRoles(Roles.Admin)]
public sealed class GetBroadcastNotificationsQuery
{
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}

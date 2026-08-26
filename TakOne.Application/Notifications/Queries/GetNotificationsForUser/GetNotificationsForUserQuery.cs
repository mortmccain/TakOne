using TakOne.Application.Common.Authorization;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Queries.GetNotificationsForUser;

/// <summary>
/// Paginated list query for the current user's notifications, newest-first.
/// Returns <see cref="PaginatedResult{NotificationDto}"/> (bare, NOT wrapped
/// in <c>Result&lt;&gt;</c>) — auth failures surface as an empty page (same
/// pattern as <c>GetSalesPaginatedQuery</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE</b>: the handler resolves the current user's Id from
/// <c>ICurrentUserService</c> and asks the repository only for that user's
/// notifications. The repository never returns another user's notification
/// via this query.
/// </para>
/// <para>
/// <para>
/// <b>AUTHORIZATION</b>: <c>[RequireAuthentication]</c> — any authenticated
/// user can list their own notifications. The handler does NOT honor a
/// caller-supplied <c>userId</c> (anti-CSRF: prevents "list someone else's
/// inbox" attacks).
/// </para>
/// <para>
/// <b>UNREAD-ONLY FILTER</b>: when <see cref="UnreadOnly"/> is true, the
/// handler skips read notifications — used by the bell-icon's "unread"
/// segmented view in the UI.
/// </para>
/// </remarks>
[RequireAuthentication]
public sealed class GetNotificationsForUserQuery
{
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public bool UnreadOnly { get; init; }
}

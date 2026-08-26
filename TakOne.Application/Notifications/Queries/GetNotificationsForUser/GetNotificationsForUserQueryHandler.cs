using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Queries.GetNotificationsForUser;

/// <summary>
/// Handler for <see cref="GetNotificationsForUserQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE</b>: always scoped to <c>currentUser.UserId</c> — the
/// repository's <c>GetPaginatedForUserAsync</c> filters by that Id.
/// The caller cannot snoop another user's inbox via this query.
/// </para>
/// <para>
/// <b>RETURN TYPE</b>: bare <see cref="PaginatedResult{T}"/> (NOT
/// wrapped in <c>Result&lt;&gt;</c>) — matches the
/// <c>GetSalesPaginatedQuery</c> contract so the UI can call
/// <c>InvokeAsync&lt;PaginatedResult&lt;NotificationDto&gt;&gt;</c>.
/// Auth failures return an empty page (warning logged).
/// </para>
/// </remarks>
public sealed class GetNotificationsForUserQueryHandler
{
    private const int MaxPageSize = 100;

    public static async Task<PaginatedResult<NotificationDto>> HandleAsync(
        GetNotificationsForUserQuery query,
        ICurrentUserService currentUser,
        INotificationRepository notificationRepository,
        ILogger<GetNotificationsForUserQueryHandler> logger,
        CancellationToken cancellationToken)
    {
        // 0. Defense-in-depth auth check.
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetNotificationsForUser: unauthenticated call rejected.");
            return new PaginatedResult<NotificationDto>(
                Array.Empty<NotificationDto>(), 0, 1, 1);
        }

        var pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
        var pageSize = query.PageSize < 1
            ? 20
            : query.PageSize > MaxPageSize
                ? MaxPageSize
                : query.PageSize;

        // Delegate to the repository — the repo's contract guarantees the
        // returned page is scoped to currentUser.UserId (no leakage).
        var page = await notificationRepository.GetPaginatedForUserAsync(
            currentUser.UserId,
            pageNumber,
            pageSize,
            query.UnreadOnly,
            cancellationToken);

        // Project aggregates → DTOs. Same shape as the SaleListItemDto
        // projection in GetSalesPaginatedQueryHandler. Includes the
        // Title/Message/BroadcastId fields (null for sale-lifecycle
        // notifications; populated for Broadcast/AppUpdate fanout rows
        // so the UI can render the admin-authored text directly).
        var dtos = page.Items
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Kind = n.Kind,
                SaleId = n.SaleId,
                SaleDisplayNumber = n.SaleDisplayNumber,
                ActorName = n.ActorName,
                Reason = n.Reason,
                Title = n.Title,
                Message = n.Message,
                BroadcastId = n.BroadcastId,
                CreatedAtUtc = n.CreatedAtUtc,
                ReadAtUtc = n.ReadAtUtc
            })
            .ToList();

        return new PaginatedResult<NotificationDto>(
            dtos, page.TotalCount, pageNumber, pageSize);
    }
}

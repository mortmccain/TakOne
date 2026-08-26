using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;

namespace TakOne.Application.Notifications.Queries.GetUnreadNotificationCount;

/// <summary>
/// Handler for <see cref="GetUnreadNotificationCountQuery"/>.
/// </summary>
/// <remarks>
/// Returns an <c>int</c> directly (matches the query's bare-int contract).
/// Auth failure returns 0 (silent — UI shows no badge). The repository's
/// <c>GetUnreadCountAsync</c> is a filtered COUNT(*) on the
/// <c>(UserId, ReadAtUtc)</c> index — fast.
/// </remarks>
public sealed class GetUnreadNotificationCountQueryHandler
{
    public static async Task<int> HandleAsync(
        GetUnreadNotificationCountQuery query,
        ICurrentUserService currentUser,
        INotificationRepository notificationRepository,
        ILogger<GetUnreadNotificationCountQueryHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetUnreadNotificationCount: unauthenticated call rejected.");
            return 0;
        }

        return await notificationRepository.GetUnreadCountAsync(
            currentUser.UserId, cancellationToken);
    }
}

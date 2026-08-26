using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Commands.MarkAllNotificationsAsRead;

/// <summary>
/// Handler for <see cref="MarkAllNotificationsAsReadCommand"/>.
/// </summary>
/// <remarks>
/// Delegates to the repository's <c>MarkAllAsReadAsync(userId)</c> —
/// a single <c>UPDATE Notifications SET ReadAtUtc = SYSUTCDATETIME()
/// WHERE UserId = @u AND ReadAtUtc IS NULL</c> statement. No domain
/// events raised (no per-notification broadcast — the user's own UI
/// will re-query the unread count on next render).
/// </remarks>
public sealed class MarkAllNotificationsAsReadCommandHandler
{
    public static async Task<Result<int>> HandleAsync(
        MarkAllNotificationsAsReadCommand command,
        ICurrentUserService currentUser,
        INotificationRepository notificationRepository,
        ILogger<MarkAllNotificationsAsReadCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            return Result<int>.Failure("Authentication required.");
        }

        var affected = await notificationRepository.MarkAllAsReadAsync(
            currentUser.UserId, cancellationToken);

        logger.LogInformation(
            "MarkAllNotificationsAsRead: marked {Count} notification(s) as read for user {UserId}.",
            affected, currentUser.UserId);

        return Result<int>.Success(affected);
    }
}

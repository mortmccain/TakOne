using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Errors;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Commands.MarkNotificationAsRead;

/// <summary>
/// Handler for <see cref="MarkNotificationAsReadCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE GUARD</b>: the repository's <c>GetByIdForUserAsync</c>
/// returns null if the notification doesn't belong to the caller —
/// defensive against CSRF. A 404 result is returned in that case (same
/// shape as a missing-notification result).
/// </para>
/// <para>
/// <b>NO BROADCAST</b>: marking read is a pure UI-state mutation — no
/// domain event raised (no <c>NotificationCreatedDomainEvent</c> needed
/// because no new row is created; the existing row's
/// <c>ReadAtUtc</c> just gets set). The recipient's other devices
/// (other tabs, mobile + PC) don't need a SignalR ping — they re-query
/// the unread count on next render anyway.
/// </para>
/// <para>
/// <b>STABLE ERROR CODES</b>: returns culture-neutral codes from
/// <see cref="NotificationErrors"/> (NOT hardcoded English) so the UI
/// layer can localize per the user's <c>CurrentUICulture</c>. This is
/// the project convention — matches <c>CartConflictErrors.Format()</c>,
/// <c>PurchaseLimitErrors.Format()</c>, etc.
/// </para>
/// </remarks>
public sealed class MarkNotificationAsReadCommandHandler
{
    public static async Task<Result> HandleAsync(
        MarkNotificationAsReadCommand command,
        ICurrentUserService currentUser,
        INotificationRepository notificationRepository,
        IUnitOfWork unitOfWork,
        ILogger<MarkNotificationAsReadCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            return Result.Failure(NotificationErrors.FormatAuthRequired());
        }

        // The repository's contract guarantees this only returns a row
        // whose UserId == currentUser.UserId. A caller passing someone
        // else's notificationId gets null (404-shaped result), preventing
        // cross-user reads via this command.
        var notification = await notificationRepository.GetByIdForUserAsync(
            command.NotificationId, currentUser.UserId, cancellationToken);

        if (notification is null)
        {
            logger.LogWarning(
                "MarkNotificationAsRead: notification {NotificationId} not found for user {UserId}.",
                command.NotificationId, currentUser.UserId);
            return Result.Failure(NotificationErrors.FormatNotFound());
        }

        // Idempotent — the domain's MarkAsRead is a no-op if already read.
        notification.MarkAsRead();

        // IUnitOfWork is required here because Wolverine's AutoApplyTransactions
        // policy enrolls this handler in an EF Core transaction; the
        // SaveChangesAsync call inside that transaction is what persists
        // the ReadAtUtc mutation. (The NotificationRepository resolves
        // ApplicationDbContext via DI, but Wolverine's transactional
        // middleware needs SaveChangesAsync called explicitly here.)
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}

using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Errors;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Commands.DeleteNotification;

/// <summary>
/// Handler for <see cref="DeleteNotificationCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>DELEGATES THE SCOPE TO SQL</b>: unlike
/// <c>MarkNotificationAsReadCommandHandler</c> (which loads the row
/// first because the DOMAIN method mutates it), a delete needs no
/// loaded aggregate — <see cref="INotificationRepository.DeleteForUserAsync"/>
/// performs the scoped <c>DELETE</c> directly and reports whether a
/// row went away. One round trip, no change-tracker ceremony.
/// </para>
/// <para>
/// <b>NOT FOUND = NOT YOURS</b>: both cases return the same
/// <see cref="NotificationErrors.FormatNotFound"/> failure — the caller
/// can't enumerate other users' notification ids.
/// </para>
/// <para>
/// <b>UNIT OF WORK</b>: <c>ExecuteDeleteAsync</c> saves inside its own
/// implicit transaction, but Wolverine's AutoApplyTransactions policy
/// still enrolls this handler — the repository call IS the persistence,
/// so no explicit SaveChanges is needed here.
/// </para>
/// </remarks>
public sealed class DeleteNotificationCommandHandler
{
    public static async Task<Result> HandleAsync(
        DeleteNotificationCommand command,
        ICurrentUserService currentUser,
        INotificationRepository notificationRepository,
        ILogger<DeleteNotificationCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            return Result.Failure(NotificationErrors.FormatAuthRequired());
        }

        var deleted = await notificationRepository.DeleteForUserAsync(
            command.NotificationId, currentUser.UserId, cancellationToken);

        if (!deleted)
        {
            logger.LogWarning(
                "DeleteNotification: notification {NotificationId} not found for user {UserId}.",
                command.NotificationId, currentUser.UserId);
            return Result.Failure(NotificationErrors.FormatNotFound());
        }

        return Result.Success();
    }
}

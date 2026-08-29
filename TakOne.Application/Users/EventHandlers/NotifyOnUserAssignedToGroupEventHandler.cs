using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Domain.Users.Events;

namespace TakOne.Application.Users.EventHandlers;

/// <summary>
/// Wolverine handler for <see cref="UserAssignedToGroupDomainEvent"/>.
/// Creates one <see cref="Notification"/> row (Kind =
/// <see cref="NotificationKind.GroupChanged"/>) for the affected user,
/// with the NEW group's display name in the notification's
/// <see cref="Notification.Reason"/> field (rendered by the UI as the
/// card's context sub-line).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS HANDLER EXISTS (Round 2 feature).</b> A customer's group
/// determines their monthly salary budget, their per-product purchase
/// limits, and their salary currency. Before this handler, reassigning a
/// user's group changed every one of those constraints SILENTLY — the
/// user's next add-to-cart clamped to different limits (or failed a
/// currency check) with no explanation. This notification closes that
/// gap: the user learns their group changed and that their budget and
/// purchase limits may have been updated.
/// </para>
/// <para>
/// <b>NO-OP SUPPRESSION</b>: <see cref="Entities.User.AssignToGroup"/>
/// raises the event unconditionally, including when the assigned group is
/// the group the user is ALREADY in (a same-group reassignment changes
/// nothing). The handler skips the notification for that case — it would
/// be pure noise.
/// </para>
/// <para>
/// <b>NO DEDUP LOOKUP</b>: unlike the sale-lifecycle handlers, there is no
/// <c>ExistsAsync</c> pre-check. The dedup unique index
/// (<c>UX_Notifications_UserId_SaleId_Kind</c>) is filtered to
/// <c>WHERE SaleId IS NOT NULL</c> and this kind always has a null
/// SaleId, so there are no dedup semantics to honor — each genuine
/// reassignment is a distinct, meaningful row.
/// </para>
/// <para>
/// <b>TRANSACTIONAL SEMANTICS</b>: Wolverine's transactional outbox writes
/// the <see cref="UserAssignedToGroupDomainEvent"/> atomically with the
/// originating <c>AssignUserToGroupCommandHandler</c>'s
/// <c>SaveChangesAsync</c>. This handler then runs in its own EF Core
/// transaction to persist the Notification row. If the originating
/// transaction rolls back, this handler never runs — no false
/// notification.
/// </para>
/// <para>
/// <b>BROADCAST IS DECOUPLED</b>: <see cref="Notification.Create"/> raises
/// <see cref="NotificationCreatedDomainEvent"/>, which the dedicated
/// <c>NotificationCreatedBroadcastHandler</c> consumes to ping the SignalR
/// hub — the affected user sees the notification live, even mid-session.
/// </para>
/// <para>
/// <b>GROUP NAME RESOLUTION</b>: the event carries group IDs only. The
/// handler resolves the NEW group's display name for the sub-line via a
/// single indexed read. If the group no longer exists (deleted between
/// assignment and notification — extremely unlikely since assignment
/// requires an existing group), the sub-line is simply omitted; the
/// notification text itself remains meaningful without it.
/// </para>
/// </remarks>
public sealed class NotifyOnUserAssignedToGroupEventHandler
{
    public static async Task HandleAsync(
        UserAssignedToGroupDomainEvent @event,
        ICustomerGroupRepository customerGroupRepository,
        INotificationRepository notificationRepository,
        IUnitOfWork unitOfWork,
        ILogger<NotifyOnUserAssignedToGroupEventHandler> logger,
        CancellationToken cancellationToken)
    {
        // IUnitOfWork is injected as a marker for Wolverine's
        // AutoApplyTransactions policy (see other NotifyOn* handlers).

        // No-op suppression: same-group reassignment. AssignToGroup raises
        // the event unconditionally; a notification saying "your group was
        // changed" when nothing changed would be pure noise.
        if (@event.PreviousGroupId == @event.NewGroupId)
        {
            logger.LogDebug(
                "GroupChanged notification suppressed for user {UserId}: group {GroupId} unchanged (no-op reassignment).",
                @event.UserId, @event.NewGroupId);
            return;
        }

        // Resolve the new group's display name for the context sub-line.
        // A missing group (hard-delete race) degrades gracefully — the
        // notification renders without the sub-line.
        var newGroup = await customerGroupRepository.GetByIdReadOnlyAsync(
            @event.NewGroupId, cancellationToken);
        var newGroupName = newGroup?.Name;

        var notification = Notification.Create(
            userId: @event.UserId,
            kind: NotificationKind.GroupChanged,
            saleId: null,
            saleDisplayNumber: null,
            actorName: null,
            reason: newGroupName);

        await notificationRepository.AddAsync(notification, cancellationToken);

        logger.LogInformation(
            "GroupChanged notification created for user {UserId} (previous group {PreviousGroupId} → new group {NewGroupId} '{NewGroupName}').",
            @event.UserId, @event.PreviousGroupId, @event.NewGroupId, newGroupName);
    }
}

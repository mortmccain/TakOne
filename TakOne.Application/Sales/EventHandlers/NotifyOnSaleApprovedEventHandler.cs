using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Domain.Sales.Events;

namespace TakOne.Application.Sales.EventHandlers;

/// <summary>
/// Wolverine handler for <see cref="SaleApprovedDomainEvent"/>.
/// Creates <see cref="Notification"/> rows for the SCOPED recipients:
///   1. The customer (their order {number} was approved by {approver}).
///   2. The approver (you approved order {number} — their action log).
/// </summary>
/// <remarks>
/// <para>
/// <b>TRANSACTIONAL SEMANTICS</b>: Wolverine's transactional outbox
/// writes the <see cref="SaleApprovedDomainEvent"/> message to the
/// <c>wolverine_messages</c> table atomically with the originating
/// <c>ApproveSaleCommandHandler</c>'s <c>SaveChangesAsync</c>. This
/// handler then runs asynchronously in its OWN EF Core transaction
/// (created by Wolverine's transactional middleware) to persist the
/// Notification rows. If the originating Approve transaction rolls back,
/// the outbox entry rolls back too — this handler never runs. No false
/// notification.
/// </para>
/// <para>
/// <b>ACTOR NAME + SALE NUMBER LOOKUP</b>: the domain event carries
/// only Guids (Approver + SaleId). The handler looks up both the
/// approver's name AND the sale's display number via the repos — two
/// single-row index seeks, acceptable for the infrequent approve event.
/// If either lookup fails (user/sale deleted mid-transaction), the
/// notification is still created with null fields — the UI handles null
/// gracefully.
/// </para>
/// <para>
/// <b>SCOPING</b>: customers can never approve their own sales (enforced
/// by <c>[RequireRoles(Employee, Manager, Admin)]</c> on
/// <c>ApproveSaleCommand</c>), so <c>ApprovedByUserId != CustomerId</c>
/// always. No self-buy short-circuit needed here.
/// </para>
/// <para>
/// <b>BROADCAST IS DECOUPLED</b>: see
/// <see cref="NotifyOnSaleSubmittedEventHandler"/>'s class doc — the
/// SignalR broadcast is handled by a separate
/// <c>NotificationCreatedBroadcastHandler</c> subscribed to
/// <see cref="NotificationCreatedDomainEvent"/>.
/// </para>
/// </remarks>
public sealed class NotifyOnSaleApprovedEventHandler
{
    public static async Task HandleAsync(
        SaleApprovedDomainEvent @event,
        INotificationRepository notificationRepository,
        IUserRepository userRepository,
        ISaleRepository saleRepository,
        IUnitOfWork unitOfWork,
        ILogger<NotifyOnSaleApprovedEventHandler> logger,
        CancellationToken cancellationToken)
    {
        // IUnitOfWork is injected as a marker so Wolverine's
        // AutoApplyTransactions policy enrolls this handler in an EF Core
        // transaction. We do NOT call unitOfWork.SaveChangesAsync here —
        // Wolverine's transactional middleware calls it after the handler
        // returns, persisting the new Notification row(s) atomically.

        // Look up the approver's name + the sale's display number ONCE —
        // both the customer's notification and the approver's notification
        // share this data (different message templates, same actor + sale).
        var approver = await userRepository.GetByIdAsync(
            @event.ApprovedByUserId, cancellationToken);
        var approverName = approver?.FullName;

        var sale = await saleRepository.GetByIdAsync(@event.SaleId, cancellationToken);
        var saleDisplayNumber = sale?.SaleNumber?.Value;

        // ── 1. Notify the customer ("your order was approved by {approver}"). ──
        await CreateForUserIfNotExistsAsync(
            userId: @event.CustomerId,
            kind: NotificationKind.SaleApproved,
            saleId: @event.SaleId,
            saleDisplayNumber: saleDisplayNumber,
            actorName: approverName,
            notificationRepository: notificationRepository,
            logger: logger,
            cancellationToken: cancellationToken);

        // ── 2. Notify the approver ("you approved order {number}"). ──
        await CreateForUserIfNotExistsAsync(
            userId: @event.ApprovedByUserId,
            kind: NotificationKind.SaleApproved,
            saleId: @event.SaleId,
            saleDisplayNumber: saleDisplayNumber,
            actorName: null, // self-name omitted
            notificationRepository: notificationRepository,
            logger: logger,
            cancellationToken: cancellationToken);
    }

    private static async Task CreateForUserIfNotExistsAsync(
        Guid userId,
        NotificationKind kind,
        Guid saleId,
        string? saleDisplayNumber,
        string? actorName,
        INotificationRepository notificationRepository,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (await notificationRepository.ExistsAsync(userId, saleId, kind, cancellationToken))
        {
            logger.LogDebug(
                "Notification ({Kind}, sale={SaleId}, user={UserId}) already exists — skipping (idempotent).",
                kind, saleId, userId);
            return;
        }

        var notification = Notification.Create(
            userId: userId,
            kind: kind,
            saleId: saleId,
            saleDisplayNumber: saleDisplayNumber,
            actorName: actorName,
            reason: null);

        await notificationRepository.AddAsync(notification, cancellationToken);
    }
}

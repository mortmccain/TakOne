using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Domain.Sales.Events;

namespace TakOne.Application.Sales.EventHandlers;

/// <summary>
/// Wolverine handler for <see cref="SaleInvoicedDomainEvent"/>.
/// Creates <see cref="Notification"/> rows for the SCOPED recipients:
///   1. The customer (their order {number} is ready for pickup / delivered).
///   2. The invoicer (you invoiced order {number} — their action log).
/// </summary>
/// <remarks>
/// <para>
/// <b>TRANSACTIONAL SEMANTICS</b>: Wolverine's transactional outbox
/// writes the <see cref="SaleInvoicedDomainEvent"/> message to the
/// <c>wolverine_messages</c> table atomically with the originating
/// <c>MarkAsInvoicedCommandHandler</c>'s <c>SaveChangesAsync</c>. This
/// handler then runs asynchronously in its OWN EF Core transaction
/// to persist the Notification rows. If the originating transaction
/// rolls back, this handler never runs. No false notification.
/// </para>
/// <para>
/// <b>SCOPING</b>: customers can never invoice their own sales (enforced
/// by <c>[RequireRoles(Employee, Manager, Admin)]</c> on
/// <c>MarkAsInvoicedCommand</c>), so <c>InvoicedByUserId != CustomerId</c>
/// always. No self-buy short-circuit.
/// </para>
/// <para>
/// <b>BROADCAST IS DECOUPLED</b>: see
/// <see cref="NotifyOnSaleSubmittedEventHandler"/>'s class doc — the
/// SignalR broadcast is handled by a separate
/// <c>NotificationCreatedBroadcastHandler</c> subscribed to
/// <see cref="NotificationCreatedDomainEvent"/>.
/// </para>
/// </remarks>
public sealed class NotifyOnSaleInvoicedEventHandler
{
    public static async Task HandleAsync(
        SaleInvoicedDomainEvent @event,
        INotificationRepository notificationRepository,
        IUserRepository userRepository,
        ISaleRepository saleRepository,
        IUnitOfWork unitOfWork,
        ILogger<NotifyOnSaleInvoicedEventHandler> logger,
        CancellationToken cancellationToken)
    {
        // IUnitOfWork is injected as a marker for Wolverine's
        // AutoApplyTransactions policy (see other NotifyOn* handlers).

        var invoicer = await userRepository.GetByIdAsync(
            @event.InvoicedByUserId, cancellationToken);
        var invoicerName = invoicer?.FullName;

        var sale = await saleRepository.GetByIdAsync(@event.SaleId, cancellationToken);
        var saleDisplayNumber = sale?.SaleNumber?.Value;

        // SaleInvoicedDomainEvent doesn't carry CustomerId — we resolve
        // it from the loaded sale (same lookup that gave us the
        // SaleDisplayNumber, so no extra round-trip).
        var customerId = sale?.CustomerId ?? Guid.Empty;
        if (customerId != Guid.Empty)
        {
            // ── 1. Notify the customer ("your order is ready for pickup"). ──
            await CreateForUserIfNotExistsAsync(
                userId: customerId,
                kind: NotificationKind.SaleInvoiced,
                saleId: @event.SaleId,
                saleDisplayNumber: saleDisplayNumber,
                actorName: invoicerName,
                notificationRepository: notificationRepository,
                logger: logger,
                cancellationToken: cancellationToken);
        }

        // ── 2. Notify the invoicer ("you invoiced order {number}"). ──
        await CreateForUserIfNotExistsAsync(
            userId: @event.InvoicedByUserId,
            kind: NotificationKind.SaleInvoiced,
            saleId: @event.SaleId,
            saleDisplayNumber: saleDisplayNumber,
            actorName: null,
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

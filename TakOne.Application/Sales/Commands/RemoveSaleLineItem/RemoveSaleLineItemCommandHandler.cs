using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.RemoveSaleLineItem;

public sealed class RemoveSaleLineItemCommandHandler
{
    public static async Task<Result> HandleAsync(
        RemoveSaleLineItemCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        ICartMutationLock cartMutationLock,
        IUnitOfWork unitOfWork,
        ILogger<RemoveSaleLineItemCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        // Need line items loaded so the aggregate can find the line to remove.
        // Loaded BEFORE the lock — we need sale.CustomerId to know whose lock
        // to acquire, and the ownership check should not block on the lock.
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        if (sale.CreatedByUserId != currentUser.UserId)
        {
            logger.LogWarning(
                "RemoveSaleLineItem: user {UserId} attempted to modify sale {SaleId} owned by {OwnerId}.",
                currentUser.UserId, sale.Id, sale.CreatedByUserId);

            return Result.Failure("You can only modify your own drafts.");
        }

        if (sale.Status != SaleStatus.Draft)
        {
            return Result.Failure(
                $"Line items can only be removed from a Draft sale. This sale is currently '{sale.Status}'.");
        }

        // ------------------------------------------------------------------
        // ACQUIRE PER-USER CART MUTATION LOCK (Step 4 wiring).
        //
        // Even though RemoveSaleLineItem only frees budget (never exceeds
        // it), the lock prevents the following race: a concurrent
        // AddItemToSale invocation reads the sale's line items BEFORE our
        // remove commits, computes its "existing line quantity" with the
        // soon-to-be-removed line included, and then over-counts stock.
        // The lock serializes the two operations.
        //
        // Acquired on sale.CustomerId (NOT currentUser.UserId) so that
        // staff-editing-on-behalf serializes on the customer's lock.
        // ------------------------------------------------------------------
        await using var _cartLockHandle = await cartMutationLock.AcquireAsync(sale.CustomerId, cancellationToken);

        // Re-load the sale after acquiring the lock — a concurrent
        // invocation may have added / removed lines or even submitted the
        // sale. The first load was needed to validate ownership + status
        // BEFORE blocking on the lock.
        sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null || sale.Status != SaleStatus.Draft)
        {
            logger.LogWarning(
                "RemoveSaleLineItem: sale {SaleId} changed state after acquiring cart lock (was Draft, now {Status}).",
                command.SaleId, sale?.Status.ToString() ?? "<null>");
            return Result.Failure(
                "This cart was modified by another session. Refresh the page and try again.");
        }

        // Defensive check before delegating to the aggregate. The aggregate's
        // RemoveLineItem throws DomainException if the line doesn't exist,
        // which middleware would convert to a failure — but giving a clearer
        // message here is friendlier.
        var lineExists = sale.LineItems.Any(li => li.Id == command.LineItemId);
        if (!lineExists)
        {
            return Result.Failure(
                $"Line item '{command.LineItemId}' was not found on sale '{sale.SaleNumber}'.");
        }

        sale.RemoveLineItem(command.LineItemId);

        // ------------------------------------------------------------------
        // GHOST-DRAFT GUARD:
        //   If the line we just removed was the LAST line on this draft, the
        //   sale is now empty (zero line items, Total = 0). Persisting such
        //   a draft creates a "ghost draft": a real Sales row in Draft status
        //   that the Cart UI renders as empty (because CartDto with 0 lines
        //   looks identical to "no cart"), but that subsequent Add-to-cart
        //   attempts will find via GetActiveDraftForUserAsync — sending them
        //   down the APPEND path on a draft the user can't see.
        //
        //   The original bug: the ghost draft persisted, blocked all future
        //   cart additions, and was invisible to the user. Fix: hard-delete
        //   the now-empty draft so the user's next Add-to-cart starts fresh.
        //
        //   We ONLY do this for drafts (the Status check at the top already
        //   guarantees Status == Draft). SaleRepository.DeleteAsync adds a
        //   second defensive Draft-only check.
        // ------------------------------------------------------------------
        if (sale.LineItems.Count == 0)
        {
            await saleRepository.DeleteAsync(sale, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "RemoveSaleLineItem: line {LineItemId} was the last line on draft {SaleId}; " +
                "draft hard-deleted to prevent ghost-draft state.",
                command.LineItemId, sale.Id);

            return Result.Success();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RemoveSaleLineItem: line {LineItemId} removed from sale {SaleId}.",
            command.LineItemId, sale.Id);

        return Result.Success();
    }
}
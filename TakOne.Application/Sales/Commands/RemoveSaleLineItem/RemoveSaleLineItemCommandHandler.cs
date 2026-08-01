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
        IUnitOfWork unitOfWork,
        ILogger<RemoveSaleLineItemCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        // Need line items loaded so the aggregate can find the line to remove.
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
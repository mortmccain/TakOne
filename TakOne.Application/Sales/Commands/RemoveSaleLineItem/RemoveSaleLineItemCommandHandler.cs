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

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RemoveSaleLineItem: line {LineItemId} removed from sale {SaleId}.",
            command.LineItemId, sale.Id);

        return Result.Success();
    }
}
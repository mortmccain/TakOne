using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.SubmitSale;

public sealed class SubmitSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        SubmitSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IUnitOfWork unitOfWork,
        ILogger<SubmitSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        // Load with line items because the aggregate's Submit() requires
        // at least one line item and a positive total — it will inspect
        // the line items collection.
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // ------------------------------------------------------------------
        // The one forbidden thing: a sale must be submitted by its own
        // creator. Customer creates draft + employee submits = NOT allowed.
        // Employee creates on behalf + employee submits = allowed (same person).
        // ------------------------------------------------------------------
        if (sale.CreatedByUserId != currentUser.UserId)
        {
            logger.LogWarning(
                "SubmitSale: user {UserId} attempted to submit sale {SaleId} created by {CreatorId}. " +
                "Only the creator can submit a sale.",
                currentUser.UserId, sale.Id, sale.CreatedByUserId);

            return Result.Failure(
                "Only the sale's creator can submit it. " +
                "If you are a sales employee creating a sale on behalf of a customer, " +
                "submit the sale yourself from the sales-employee page.");
        }

        // Delegate to the aggregate. Submit() enforces:
        //   - sale is in Draft status (throws otherwise)
        //   - sale has at least one line item (throws otherwise)
        //   - sale total is positive (throws otherwise)
        // DomainException is caught by middleware and converted to Result.Failure.
        sale.Submit();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "SubmitSale: sale {SaleId} ({SaleNumber}) submitted by user {UserId}.",
            sale.Id, sale.SaleNumber, currentUser.UserId);

        return Result.Success();
    }
}
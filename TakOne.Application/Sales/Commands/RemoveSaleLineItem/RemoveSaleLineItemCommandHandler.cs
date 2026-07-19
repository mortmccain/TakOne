using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.RemoveSaleLineItem;

public static class RemoveSaleLineItemCommandHandler
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
            return Result.Failure("Authentication required.");

        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        if (sale.CustomerId != currentUser.UserId)
            return Result.Failure("You can only modify your own sale.");

        sale.RemoveLineItem(command.LineItemId);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Removed line {LineItemId} from sale {SaleId}.",
            command.LineItemId, command.SaleId);

        return Result.Success();
    }
}
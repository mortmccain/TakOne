using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.CancelSale;

public static class CancelSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        CancelSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IUnitOfWork unitOfWork,
        ILogger<CancelSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        var sale = await saleRepository.GetByIdAsync(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        sale.Cancel(currentUser.UserId, command.Reason);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sale {SaleId} cancelled by user {UserId}. Reason: {Reason}",
            command.SaleId, currentUser.UserId, command.Reason);

        return Result.Success();
    }
}
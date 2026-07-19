using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.MarkSaleAsInvoiced;

public static class MarkAsInvoicedCommandHandler
{
    public static async Task<Result> HandleAsync(
        MarkSaleAsInvoicedCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IUnitOfWork unitOfWork,
        ILogger<MarkSaleAsInvoicedCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        var sale = await saleRepository.GetByIdAsync(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        sale.MarkAsInvoiced(currentUser.UserId);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sale {SaleId} marked as invoiced by user {UserId}.",
            command.SaleId, currentUser.UserId);

        return Result.Success();
    }
}
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.SubmitSale;

public static class SubmitSaleCommandHandler
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
            return Result.Failure("Authentication required.");

        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        if (sale.CustomerId != currentUser.UserId)
            return Result.Failure("You can only submit your own sale.");

        // No userId argument — the submitter is always the sale's creator.
        sale.Submit();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sale {SaleId} submitted by user {UserId}.",
            command.SaleId, currentUser.UserId);

        return Result.Success();
    }
}
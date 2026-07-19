using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.ApproveSale;

public static class ApproveSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        ApproveSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<ApproveSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // Role check is done by AuthorizationMiddleware via [RequireRoles(...)].

        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        sale.Approve(currentUser.UserId);

        // Decrease stock for each line item — stock is held only when the sale
        // is committed, not when items are added to the cart.
        foreach (var line in sale.LineItems)
        {
            var product = await productRepository.GetByIdAsync(line.ProductId, cancellationToken);
            if (product is null)
                return Result.Failure(
                    $"Product '{line.ProductId}' on line {line.LineNumber} no longer exists.");

            // DomainException (insufficient stock) is caught by middleware.
            product.DecreaseStock(line.Quantity);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Sale {SaleId} approved by user {UserId}.",
            command.SaleId, currentUser.UserId);

        return Result.Success();
    }
}
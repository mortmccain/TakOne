using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Commands.IncreaseProductStock;

public sealed class IncreaseProductStockCommandHandler
{
    public static async Task<Result> HandleAsync(
        IncreaseProductStockCommand command,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<IncreaseProductStockCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // Capture the pre-increase stock for logging — useful for audit trail.
        var stockBefore = product.StockQuantity;

        // Delegate to the aggregate. IncreaseStock throws DomainException
        // if quantity ≤ 0 (already caught by validator, but the domain never
        // trusts the caller — defense in depth).
        product.IncreaseStock(command.Quantity);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "IncreaseProductStock: product {ProductId} stock increased by {Qty}. " +
            "Before: {Before}, after: {After}.",
            product.Id, command.Quantity, stockBefore, product.StockQuantity);

        return Result.Success();
    }
}
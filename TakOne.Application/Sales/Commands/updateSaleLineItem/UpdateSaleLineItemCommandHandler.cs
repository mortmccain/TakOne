using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.updateSaleLineItem;

/// <summary>
/// Updates the quantity of a line item on the current user's Draft Sale.
/// Re-enforces the per-group purchase limit using the line's Product.
/// </summary>
public static class UpdateSaleLineItemCommandHandler
{
    public static async Task<Result> HandleAsync(
        UpdateSaleLineItemCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<UpdateSaleLineItemCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
            return Result.Failure("Authentication required.");

        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        if (sale.CustomerId != currentUser.UserId)
            return Result.Failure("You can only modify your own sale.");

        var lineItem = sale.LineItems.FirstOrDefault(li => li.Id == command.LineItemId);
        if (lineItem is null)
            return Result.Failure($"Line item '{command.LineItemId}' was not found on this sale.");

        var product = await productRepository.GetByIdAsync(lineItem.ProductId, cancellationToken);
        if (product is null)
            return Result.Failure($"Product '{lineItem.ProductId}' was not found.");

        // Look up the per-group purchase limit (same logic as AddItemToSale).
        int? purchaseLimit = null;
        if (!string.IsNullOrWhiteSpace(currentUser.GroupName))
        {
            var limitVo = product.GetPurchaseLimitForGroup(currentUser.GroupName);
            purchaseLimit = limitVo?.Limit;
        }

        sale.UpdateLineItemQuantity(command.LineItemId, command.NewQuantity, purchaseLimit);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Updated line {LineItemId} on sale {SaleId} to quantity {Quantity}.",
            command.LineItemId, command.SaleId, command.NewQuantity);

        return Result.Success();
    }
}
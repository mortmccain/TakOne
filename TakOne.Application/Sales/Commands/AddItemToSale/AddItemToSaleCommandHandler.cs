using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.AddItemToSale;

/// <summary>
/// Adds (or increments) a product on a Draft Sale.
///
/// ENFORCEMENT FLOW:
///   1. Load the Sale (with line items) — must be a Draft owned by the current user.
///   2. Load the Product — must exist.
///   3. Look up the per-group purchase limit on the Product using the
///      current user's GroupName. If the user has no group, no limit applies.
///   4. Call sale.AddLineItem(product.Id, product.Name, quantity, product.Price, limit).
///      The Sale aggregate enforces the limit and recalculates the total.
/// </summary>
public static class AddItemToSaleCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        AddItemToSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<AddItemToSaleCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        if (!currentUser.IsAuthenticated)
            return Result.Failure("Authentication required.");

        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        // The sale must belong to the current user.
        if (sale.CustomerId != currentUser.UserId)
            return Result.Failure("You can only modify your own sale.");

        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);
        if (product is null)
            return Result.Failure($"Product '{command.ProductId}' was not found.");

        // Look up the per-group purchase limit. Null means no limit enforced.
        int? purchaseLimit = null;
        if (!string.IsNullOrWhiteSpace(currentUser.GroupName))
        {
            var limitVo = product.GetPurchaseLimitForGroup(currentUser.GroupName);
            purchaseLimit = limitVo?.Limit;
        }

        // The Sale aggregate enforces the limit and recalculates the total.
        // DomainException is caught by middleware and converted to Result.Failure.
        sale.AddLineItem
            (
            productId: product.Id,
            productName: product.Name,
            quantity: command.Quantity,
            unitPrice: product.Price,
            purchaseLimit: purchaseLimit
            );

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Added {Quantity} of product {ProductId} to sale {SaleId}.",
            command.Quantity, command.ProductId, command.SaleId);

        return Result.Success();
    }
}
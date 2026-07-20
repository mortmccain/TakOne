using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.Commands.AddItemToSale;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;
public sealed class AddItemToSaleCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        AddItemToSaleCommand command,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUserRepository userRepository,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork,
        ILogger<AddItemToSaleCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        var sale = await saleRepository.GetByIdAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // Only the creator can edit a draft.
        if (sale.CreatedByUserId != currentUser.UserId)
        {
            return Result.Failure("You can only modify your own drafts.");
        }

        if (sale.Status != SaleStatus.Draft)
        {
            return Result.Failure("Items can only be added to a draft sale.");
        }

        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        if (product.StockQuantity < command.Quantity)
        {
            return Result.Failure($"Not enough stock for '{product.Name}'.");
        }

        // Look up the customer's group to resolve per-group purchase limit.
        var customer = await userRepository.GetByIdAsync(sale.CustomerId, cancellationToken);
        int? purchaseLimit = customer?.GroupName is null
            ? null
            : product.GetPurchaseLimitForGroup(customer.GroupName);

        sale.AddLineItem(
            product.Id,
            product.Name,
            command.Quantity,
            product.Price,
            purchaseLimit);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("AddItemToSale: product {ProductId} (x{Qty}) added to sale {SaleId}.",
            product.Id, command.Quantity, sale.Id);

        return Result.Success();
    }
}
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.Commands.CreateSale;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;

public sealed class CreateSaleCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync(
        CreateSaleCommand command,
        IUserRepository userRepository,
        IProductRepository productRepository,
        ISaleRepository saleRepository,
        ISaleNumberGenerator saleNumberGenerator,
        ICurrentUserService currentUser,
        IUnitOfWork unitOfWork,
        ILogger<CreateSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // 1. Resolve the customer by worker ID (the employee types the worker ID, not the Guid).
        var customer = await userRepository.GetByWorkerIdAsync(command.CustomerWorkerId, cancellationToken);
        if (customer is null)
        {
            logger.LogWarning("CreateSale: customer with worker ID {WorkerId} not found.", command.CustomerWorkerId);
            return Result<Guid>.Failure($"No user found with worker ID '{command.CustomerWorkerId}'.");
        }

        if (!customer.IsActive)
        {
            return Result<Guid>.Failure($"User '{command.CustomerWorkerId}' is inactive and cannot be the customer of a sale.");
        }

        // 2. Creator = the authenticated user. If creator.Id == customer.Id, this is a self-buy.
        var creatorId = currentUser.UserId;
        var creatorName = currentUser.FullName;

        // 3. Generate the sale number.
        var saleNumber = await saleNumberGenerator.GenerateAsync(cancellationToken);

        // 4. Create the sale in Draft state.
        //    Sale.Create(saleNumber, customerId, customerName, createdById, createdByName)
        var sale = Sale.Create(
            saleNumber,
            customer.Id,
            customer.FullName,
            creatorId,
            creatorName);

        // 5. Add each line item — load the product, resolve the customer's group purchase limit,
        //    pass it down to the aggregate which enforces it.
        foreach (var item in command.Items)
        {
            var product = await productRepository.GetByIdAsync(item.ProductId, cancellationToken);
            if (product is null)
            {
                return Result<Guid>.Failure($"Product '{item.ProductId}' was not found.");
            }

            if (product.StockQuantity < item.Quantity)
            {
                return Result<Guid>.Failure($"Not enough stock for '{product.Name}' (requested {item.Quantity}, available {product.StockQuantity}).");
            }

            int? purchaseLimit = customer.GroupName is null
                ? null
                : product.GetPurchaseLimitForGroup(customer.GroupName);

            sale.AddLineItem(
                product.Id,
                product.Name,
                item.Quantity,
                product.Price,
                purchaseLimit);
        }

        // 6. Persist.
        await saleRepository.AddAsync(sale, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "CreateSale: sale {SaleId} ({SaleNumber}) created by {CreatorId} for customer {CustomerId} (self-buy={SelfBuy}).",
            sale.Id, sale.SaleNumber, creatorId, customer.Id, creatorId == customer.Id);

        return Result<Guid>.Success(sale.Id);
    }
}
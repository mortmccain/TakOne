using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Commands.SetProductPurchaseLimit;

public sealed class SetProductPurchaseLimitCommandHandler
{
    public static async Task<Result> HandleAsync(
        SetProductPurchaseLimitCommand command,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<SetProductPurchaseLimitCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // Delegate to the aggregate. SetPurchaseLimit:
        //   - constructs a new CustomerGroupPurchaseLimit value object
        //     (throws DomainException on invalid groupName or limit)
        //   - removes any existing limit for the same group (by GroupName match)
        //   - adds the new value object to the owned collection
        // EF Core will track the owned-collection change and persist it.
        product.SetPurchaseLimit(command.GroupName, command.Limit);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "SetProductPurchaseLimit: limit for group '{Group}' on product {ProductId} set to {Limit}.",
            command.GroupName, product.Id, command.Limit);

        return Result.Success();
    }
}
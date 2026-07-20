using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Commands.RemoveProductPurchaseLimit;

public sealed class RemoveProductPurchaseLimitCommandHandler
{
    public static async Task<Result> HandleAsync(
        RemoveProductPurchaseLimitCommand command,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<RemoveProductPurchaseLimitCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // Delegate to the aggregate. RemovePurchaseLimit is idempotent —
        // if no limit exists for the group, it's a no-op (doesn't throw).
        // We persist unconditionally: in the idempotent no-op case, EF Core
        // simply won't detect any changes and SaveChangesAsync is a null-op
        // round-trip. Acceptable cost for simpler code.
        product.RemovePurchaseLimit(command.GroupName);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RemoveProductPurchaseLimit: limit for group '{Group}' removed from product {ProductId} (if it existed).",
            command.GroupName, product.Id);

        return Result.Success();
    }
}
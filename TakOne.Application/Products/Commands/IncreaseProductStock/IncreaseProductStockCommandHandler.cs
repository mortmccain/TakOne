using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Commands.IncreaseProductStock;

public sealed class IncreaseProductStockCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        IncreaseProductStockCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<IncreaseProductStockCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        //    Defensive auth check. [RequireRoles] already rejected anonymous
        //    callers via AuthorizationMiddleware, but this handler may also
        //    be invoked from tests or a non-HTTP host — re-checking keeps
        //    the invariant honest.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("IncreaseProductStock: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            logger.LogWarning
                ("IncreaseProductStock: product {ProductId} was not found. Requested by user {UserId}.",
                command.ProductId, currentUser.UserId);

            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // Capture the pre-increase stock for logging — useful for audit trail.
        var stockBefore = product.StockQuantity;

        // Delegate to the aggregate. IncreaseStock throws DomainException
        // if quantity ≤ 0 (already caught by validator, but the domain never
        // trusts the caller — defense in depth).
        product.IncreaseStock(command.Quantity);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("IncreaseProductStock: product {ProductId} stock increased by {Qty} by user {UserId}. " + "Before: {Before}, after: {After}.",
            product.Id, command.Quantity, currentUser.UserId, stockBefore, product.StockQuantity);

        return Result.Success();
    }
}
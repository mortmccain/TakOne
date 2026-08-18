using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Commands.SetProductStock;

/// <summary>
/// Sets a Product's stock to an EXACT value (replaces the current stock).
///
/// See <see cref="SetProductStockCommand"/> for the business rule that
/// justifies rejecting quantity ≤ 0, and for why this is a separate
/// command from <see cref="TakOne.Application.Products.Commands.IncreaseProductStock.IncreaseProductStockCommand"/>
/// (additive vs absolute).
/// </summary>
public sealed class SetProductStockCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        SetProductStockCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<SetProductStockCommandHandler> logger,
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
            logger.LogWarning("SetProductStock: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            logger.LogWarning
                ("SetProductStock: product {ProductId} was not found. Requested by user {UserId}.",
                command.ProductId, currentUser.UserId);

            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // Capture pre-change stock for the audit log.
        var stockBefore = product.StockQuantity;

        // Delegate to the aggregate. AdjustStockTo throws DomainException
        // if quantity ≤ 0 (already caught by the validator's GreaterThan(0)
        // rule, but the domain never trusts the caller — defense in depth).
        // Same pattern as IncreaseProductStockCommandHandler.
        product.AdjustStockTo(command.Quantity);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("SetProductStock: product {ProductId} stock set to {Qty} by user {UserId}. " + "Before: {Before}, after: {After}.",
            product.Id, command.Quantity, currentUser.UserId, stockBefore, product.StockQuantity);

        return Result.Success();
    }
}
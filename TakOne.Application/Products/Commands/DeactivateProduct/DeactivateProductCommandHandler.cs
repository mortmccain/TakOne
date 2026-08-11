using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Commands.DeactivateProduct;

/// <summary>
/// Deactivates a Product by setting its StockQuantity to 0.
///
/// See <see cref="DeactivateProductCommand"/> for the business rule that
/// justifies "stock = 0" as the deactivation operation, and for the
/// audit/popup contract that the UI enforces before dispatching.
/// </summary>
public sealed class DeactivateProductCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        DeactivateProductCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<DeactivateProductCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check. [RequireRoles] already rejected anonymous
        //    callers via AuthorizationMiddleware, but this handler may also
        //    be invoked from tests or a non-HTTP host — re-checking keeps
        //    the invariant honest.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("DeactivateProduct: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the product. Tracked (not AsNoTracking) because we're
        //    about to mutate StockQuantity and need EF Core's change
        //    tracker to detect + persist the change.
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            logger.LogWarning
                ("DeactivateProduct: product {ProductId} was not found. Requested by user {UserId}.",
                command.ProductId, currentUser.UserId);

            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Capture pre-deactivation stock for the audit log. The previous
        //    value is NOT stored anywhere in the database — once stock is 0,
        //    the system forgets what it used to be. The UI's popup warns
        //    the user to write down the value before confirming.
        // ------------------------------------------------------------------
        var stockBefore = product.StockQuantity;

        // ------------------------------------------------------------------
        // 3. Delegate to the aggregate. SetStock(0) goes through the
        //    domain's EnsureStockQuantityValid guard (which only rejects
        //    NEGATIVE values — 0 is valid). Idempotent: setting an already-
        //    zero stock to 0 is a no-op at the aggregate level.
        // ------------------------------------------------------------------
        product.SetStock(0);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("DeactivateProduct: product {ProductId} ('{Name}') deactivated by user {UserId}. " +
             "Previous stock was {PreviousStock} (now 0).",
             product.Id, product.Name, currentUser.UserId, stockBefore);

        return Result.Success();
    }
}

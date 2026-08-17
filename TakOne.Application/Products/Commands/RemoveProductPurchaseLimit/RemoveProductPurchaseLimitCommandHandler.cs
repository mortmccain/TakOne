using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Commands.RemoveProductPurchaseLimit;

public sealed class RemoveProductPurchaseLimitCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        RemoveProductPurchaseLimitCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<RemoveProductPurchaseLimitCommandHandler> logger,
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
            logger.LogWarning("RemoveProductPurchaseLimit: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }


        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            logger.LogWarning
                ("RemoveProductPurchaseLimit: product {ProductId} was not found. Requested by user {UserId}.",
                command.ProductId, currentUser.UserId);

            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // Delegate to the aggregate. RemovePurchaseLimit is idempotent —
        // if no limit exists for the group, it's a no-op (doesn't throw).
        // We persist unconditionally: in the idempotent no-op case, EF Core
        // simply won't detect any changes and SaveChangesAsync is a null-op
        // round-trip. Acceptable cost for simpler code.
        product.RemovePurchaseLimit(command.GroupId);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("RemoveProductPurchaseLimit: limit for group {GroupId} removed from product {ProductId} (if it existed) by user {UserId}.",
            command.GroupId, product.Id, currentUser.UserId);

        return Result.Success();
    }
}
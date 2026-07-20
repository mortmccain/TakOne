using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Products.Commands.SetProductPurchaseLimit;

public sealed class SetProductPurchaseLimitCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        SetProductPurchaseLimitCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<SetProductPurchaseLimitCommandHandler> logger,
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
            logger.LogWarning("SetProductPurchaseLimit: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }


        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            logger.LogWarning("SetProductPurchaseLimit: product {ProductId} was not found. Requested by user {UserId}.",
                command.ProductId, currentUser.UserId);

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

        logger.LogInformation
            ("SetProductPurchaseLimit: limit for group '{Group}' on product {ProductId} set to {Limit} by user {UserId}.",
            command.GroupName, product.Id, command.Limit, currentUser.UserId);

        return Result.Success();
    }
}
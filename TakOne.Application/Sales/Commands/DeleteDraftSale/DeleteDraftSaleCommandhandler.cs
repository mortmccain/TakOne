using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.DeleteDraftSale;

public static class DeleteDraftSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        DeleteDraftSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IUnitOfWork unitOfWork,
        ILogger<DeleteDraftSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
            return Result.Failure("Authentication required.");

        var sale = await saleRepository.GetByIdAsync(command.SaleId, cancellationToken);
        if (sale is null)
            return Result.Failure($"Sale '{command.SaleId}' was not found.");

        if (sale.CustomerId != currentUser.UserId)
            return Result.Failure("You can only delete your own draft sale.");

        // Defensive check at the application layer: only Drafts can be deleted.
        // (The repository implementation in step 7 will also enforce this.)
        if (sale.Status != SaleStatus.Draft)
            return Result.Failure(
                $"Only Draft sales can be deleted. Current status: '{sale.Status}'.");

        await saleRepository.DeleteAsync(sale, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Draft sale {SaleId} hard-deleted by user {UserId}.",
            command.SaleId, currentUser.UserId);

        return Result.Success();
    }
}
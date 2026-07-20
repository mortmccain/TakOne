using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.DeleteDraftSale;

public sealed class DeleteDraftSaleCommandHandler
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
        {
            return Result.Failure("Authentication required.");
        }

        // No need to load with line items — we're hard-deleting the sale,
        // and EF Core will cascade-delete the line items via the relationship
        // configuration in Infrastructure.
        var sale = await saleRepository.GetByIdAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            // Idempotent: deleting a non-existent draft is a success (the
            // caller's intent — "make this draft go away" — is satisfied).
            // Alternatively we could return Failure; the choice depends on
            // UX preference. Returning success is friendlier for retries.
            logger.LogInformation(
                "DeleteDraftSale: sale {SaleId} was not found. Treating as already deleted.",
                command.SaleId);

            return Result.Success();
        }

        if (sale.Status != SaleStatus.Draft)
        {
            return Result.Failure(
                $"Only Draft sales can be deleted. This sale is currently '{sale.Status}'. " +
                "Use the cancel command for non-draft sales.");
        }

        if (sale.CreatedByUserId != currentUser.UserId)
        {
            logger.LogWarning(
                "DeleteDraftSale: user {UserId} attempted to delete sale {SaleId} owned by {OwnerId}.",
                currentUser.UserId, sale.Id, sale.CreatedByUserId);

            return Result.Failure("You can only delete your own drafts.");
        }

        // Hard delete — no soft-delete flag, no audit trail for drafts.
        await saleRepository.DeleteAsync(sale, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "DeleteDraftSale: draft sale {SaleId} ({SaleNumber}) hard-deleted by user {UserId}.",
            sale.Id, sale.SaleNumber, currentUser.UserId);

        return Result.Success();
    }
}
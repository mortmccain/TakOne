using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.DeleteDraftSale;

public sealed class DeleteDraftSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        DeleteDraftSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        ICartMutationLock cartMutationLock,
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

        // ------------------------------------------------------------------
        // ACQUIRE PER-USER CART MUTATION LOCK.
        //
        // Every other cart mutation (AddItemToSale, UpdateSaleLineItem,
        // RemoveSaleLineItem, SubmitSale, CreateOrAppendSale,
        // QuickReorderLastSale) serializes on this lock. Without it here, a
        // concurrent add-to-cart racing this delete produces an unhandled
        // DbUpdateConcurrencyException / FK error (generic "unexpected
        // error" toast) instead of the clean CartConflictErrors result the
        // other paths return. Same pattern as RemoveSaleLineItem: acquire
        // on sale.CustomerId (NOT currentUser.UserId) so staff acting on
        // behalf serializes on the customer's lock.
        // ------------------------------------------------------------------
        await using var _cartLockHandle = await cartMutationLock.AcquireAsync(sale.CustomerId, cancellationToken);

        // Re-load after acquiring the lock — a concurrent invocation may
        // have submitted the sale in the window between the first load and
        // the lock. The pre-lock validation (ownership, existence) doesn't
        // need the lock; the status check that guards the DELETE does.
        sale = await saleRepository.GetByIdAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            // Raced with another delete — same idempotent semantics as above.
            logger.LogInformation(
                "DeleteDraftSale: sale {SaleId} disappeared after acquiring cart lock. Treating as already deleted.",
                command.SaleId);

            return Result.Success();
        }

        if (sale.Status != SaleStatus.Draft)
        {
            logger.LogWarning(
                "DeleteDraftSale: sale {SaleId} changed state after acquiring cart lock (was Draft, now {Status}).",
                command.SaleId, sale.Status);

            return Result.Failure(CartConflictErrors.Format());
        }

        if (sale.CreatedByUserId != currentUser.UserId)
        {
            // Ownership can't change on an existing row, but re-assert
            // cheaply for defense-in-depth after the re-load.
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

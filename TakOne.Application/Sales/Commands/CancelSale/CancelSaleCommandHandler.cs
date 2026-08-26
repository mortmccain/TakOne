using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.CancelSale;

public sealed class CancelSaleCommandHandler
{
    /// <summary>
    /// Maximum retry attempts for the cancel + (optional) stock-restore +
    /// SaveChanges sequence. Catches <c>DbUpdateConcurrencyException</c>
    /// (sale row version conflict from a concurrent Approve on the same
    /// sale) + SQL Server unique-constraint violations.
    /// </summary>
    private const int MaxAttempts = 3;

    public static async Task<Result> HandleAsync(
        CancelSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        ISaleStateLock saleStateLock,
        IUnitOfWork unitOfWork,
        ILogger<CancelSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        if (currentUser.UserId == Guid.Empty)
        {
            return Result.Failure("Authentication required.");
        }

        // Need line items because if the sale was Approved, we have to
        // restore stock for each line.
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // Defensive: reject Drafts with a friendly message instead of letting
        // the aggregate's Cancel() throw. Drafts go through DeleteDraftSaleCommand.
        if (sale.Status == SaleStatus.Draft)
        {
            return Result.Failure(
                "Draft sales cannot be cancelled. Use the delete-draft command instead — " +
                "drafts are disposable carts and are hard-deleted, not cancelled.");
        }

        // ------------------------------------------------------------------
        // ACQUIRE PER-SALE STATE-TRANSITION LOCK + RETRY (race-condition fix).
        //
        // The critical race here is Cancel × Approve: both staff members
        // load the sale, both see Pending (or one sees Approved), and the
        // loser's `wasApproved` snapshot is stale by the time it commits
        // — causing wrong stock-restoration (e.g. the loser restores
        // stock for a sale that the winner actually approved-to-Invoiced).
        //
        // The lock serializes Cancel against concurrent Submit / Approve
        // / MarkAsInvoiced on the SAME sale. The retry catches the
        // residual cross-instance race (multiple app servers hitting the
        // same DB) via DbUpdateConcurrencyException + unique-constraint
        // violations. On retry, the lambda RE-LOADS the sale to observe
        // the new state and RE-COMPUTES wasApproved — so stock-restoration
        // is always correct against the freshest state.
        // ------------------------------------------------------------------
        await using var _saleStateLockHandle = await saleStateLock.AcquireAsync(
            sale.Id, cancellationToken);

        try
        {
            return await unitOfWork.ExecuteWithRetryAsync(
                operation: async ct =>
                {
                    // Re-load the sale fresh — a concurrent Submit / Approve
                    // may have moved the state on between our initial load
                    // (above) and our acquisition of the lock.
                    var freshSale = await saleRepository.GetByIdWithLineItemsAsync(
                        command.SaleId, ct);
                    if (freshSale is null)
                    {
                        return Result.Failure($"Sale '{command.SaleId}' was not found.");
                    }

                    if (freshSale.Status == SaleStatus.Draft)
                    {
                        return Result.Failure(
                            "Draft sales cannot be cancelled. Use the delete-draft command instead — " +
                            "drafts are disposable carts and are hard-deleted, not cancelled.");
                    }

                    // Re-capture the freshest pre-cancel status — this is
                    // the CRITICAL fix for the Cancel × Approve race.
                    var wasApproved = freshSale.Status == SaleStatus.Approved;
                    var stockRestored = false;

                    if (wasApproved)
                    {
                        var productIds = freshSale.LineItems
                            .Select(li => li.ProductId)
                            .Distinct()
                            .ToList();
                        var productsById = new Dictionary<Guid, Domain.Products.Entities.Product>();

                        foreach (var productId in productIds)
                        {
                            var product = await productRepository.GetByIdAsync(productId, ct);
                            if (product is null)
                            {
                                logger.LogWarning(
                                    "CancelSale: product {ProductId} on sale {SaleId} no longer exists. " +
                                    "Stock cannot be restored for this product. Sale will still be cancelled.",
                                    productId, freshSale.Id);
                                continue;
                            }
                            productsById[productId] = product;
                        }

                        foreach (var line in freshSale.LineItems)
                        {
                            if (productsById.TryGetValue(line.ProductId, out var product))
                            {
                                product.IncreaseStock(line.Quantity);
                                stockRestored = true;
                            }
                        }
                    }

                    // Delegate to the aggregate. Cancel enforces:
                    //   - sale is not Draft (defensive — already checked)
                    //   - sale is not Invoiced (throws — credit note instead)
                    //   - sale is not already Cancelled (throws)
                    //   - reason is non-empty (throws)
                    //   - cancelledByUserId is a non-empty Guid (throws)
                    // Throws DomainException if any guard fails — caught
                    // by the outer try/catch and returned as Result.Failure.
                    freshSale.Cancel(currentUser.UserId, command.Reason);

                    await unitOfWork.SaveChangesAsync(ct);

                    logger.LogInformation(
                        "CancelSale: sale {SaleId} ({SaleNumber}) cancelled by user {UserId}. Reason: {Reason}. " +
                        "Stock restored: {StockRestored}.",
                        freshSale.Id, freshSale.SaleNumber, currentUser.UserId, command.Reason, stockRestored);

                    return Result.Success();
                },
                maxAttempts: MaxAttempts,
                cancellationToken: cancellationToken);
        }
        catch (DomainException ex)
        {
            // The aggregate's EnsureCancellable() threw — a concurrent
            // Approve/Cancel moved the sale state (e.g. someone cancelled
            // it while we were trying to cancel). Surface as a clean
            // failure (no retry — state has irreversibly moved on).
            logger.LogWarning(
                "CancelSale: domain guard failed for sale {SaleId} (likely a concurrent state transition). " +
                "Message: {Message}",
                command.SaleId, ex.Message);
            return Result.Failure(
                "The sale's state changed before cancellation could complete. Refresh and try again.");
        }
    }
}
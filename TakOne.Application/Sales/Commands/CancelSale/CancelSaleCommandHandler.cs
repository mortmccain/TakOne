using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.CancelSale;

public sealed class CancelSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        CancelSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
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

        // Capture the pre-cancel status so we know whether stock restoration
        // is needed (only Approved sales had stock decremented at Approve time).
        var wasApproved = sale.Status == SaleStatus.Approved;
        var stockRestored = false;

        // ------------------------------------------------------------------
        // If the sale was Approved, stock was decremented at Approve time.
        // Cancellation must restore it. We do this BEFORE calling Cancel()
        // so that if any product lookup fails, the sale's state is still
        // untouched in memory.
        //
        // If the sale was Pending (not yet approved), stock was NOT
        // decremented — no restoration needed.
        // ------------------------------------------------------------------
        if (wasApproved)
        {
            // Load all distinct products on the sale, then increase stock
            // by the total quantity per product.
            var productIds = sale.LineItems.Select(li => li.ProductId).Distinct().ToList();
            var productsById = new Dictionary<Guid, Domain.Products.Entities.Product>();

            foreach (var productId in productIds)
            {
                var product = await productRepository.GetByIdAsync(productId, cancellationToken);
                if (product is null)
                {
                    // Defensive — the product existed when the sale was
                    // approved. If it's gone now, log and continue; we
                    // still want to cancel the sale.
                    logger.LogWarning(
                        "CancelSale: product {ProductId} on sale {SaleId} no longer exists. " +
                        "Stock cannot be restored for this product. Sale will still be cancelled.",
                        productId, sale.Id);
                    continue;
                }

                productsById[productId] = product;
            }

            foreach (var line in sale.LineItems)
            {
                if (productsById.TryGetValue(line.ProductId, out var product))
                {
                    product.IncreaseStock(line.Quantity);
                    stockRestored = true;
                }
            }
        }

        // Delegate to the aggregate. Cancel enforces:
        //   - sale is not Draft (defensive — we already checked above)
        //   - sale is not Invoiced (throws — issue a credit note instead)
        //   - sale is not already Cancelled (throws)
        //   - reason is non-empty (throws)
        //   - cancelledByUserId is a non-empty Guid (throws)
        sale.Cancel(currentUser.UserId, command.Reason);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "CancelSale: sale {SaleId} ({SaleNumber}) cancelled by user {UserId}. Reason: {Reason}. " +
            "Stock restored: {StockRestored}.",
            sale.Id, sale.SaleNumber, currentUser.UserId, command.Reason, stockRestored);

        return Result.Success();
    }
}
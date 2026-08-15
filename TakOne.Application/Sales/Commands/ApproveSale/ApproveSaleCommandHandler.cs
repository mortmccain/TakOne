using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.ApproveSale;

public sealed class ApproveSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        ApproveSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<ApproveSaleCommandHandler> logger,
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

        // Need line items because we have to decrement stock for each line.
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // ------------------------------------------------------------------
        // Pre-check stock for each line BEFORE calling Approve(). If we call
        // Approve() first and then fail on stock, the sale's state is
        // mutated in memory — but since we haven't called SaveChangesAsync
        // yet, nothing is persisted. Still, doing the checks up front gives
        // a cleaner error message ("not enough stock for X") instead of a
        // DomainException mid-loop.
        // ------------------------------------------------------------------
        // We load each product into a dictionary so we can reuse the same
        // instance when we decrement stock after Approve() succeeds.
        var productsById = new Dictionary<Guid, Domain.Products.Entities.Product>();
        foreach (var line in sale.LineItems)
        {
            if (productsById.ContainsKey(line.ProductId))
            {
                // Same product on multiple lines — unlikely (the aggregate
                // merges them in AddLineItem) but defensive.
                continue;
            }

            var product = await productRepository.GetByIdAsync(line.ProductId, cancellationToken);
            if (product is null)
            {
                // Product no longer exists — return a stable, culture-
                // neutral error code that the UI localizes (via the
                // CategoryDeactivatedErrors pattern — same UX: the
                // product is unavailable, remove the line). Previously
                // this was a hardcoded English string.
                return Result.Failure(
                    CategoryDeactivatedErrors.Format(line.ProductName));
            }

            // Total quantity across all lines for this product.
            var totalQuantityForProduct = sale.LineItems
                .Where(li => li.ProductId == line.ProductId)
                .Sum(li => li.Quantity);

            if (totalQuantityForProduct > product.StockQuantity)
            {
                // Not enough stock — return a stable, culture-neutral
                // error code (StockErrors.Format) so the UI can localize
                // the message into the user's language. Previously this
                // was a hardcoded English string that leaked as an
                // English error toast even in Persian (fa-IR) mode —
                // the exact bug reported by the user.
                return Result.Failure(
                    StockErrors.Format(product.Name, product.StockQuantity, totalQuantityForProduct));
            }

            productsById[line.ProductId] = product;
        }

        // ------------------------------------------------------------------
        // Transition the sale to Approved. The aggregate enforces:
        //   - sale is currently Pending (throws otherwise)
        //   - total is positive (throws otherwise)
        //   - approvedByUserId is a non-empty Guid (throws otherwise)
        // ------------------------------------------------------------------
        sale.Approve(currentUser.UserId);

        // ------------------------------------------------------------------
        // Decrement stock for each line. Done AFTER Approve() so we only
        // mutate products if the sale transition succeeded. The whole
        // operation (sale status change + product stock decrements) commits
        // in a single SaveChangesAsync transaction.
        // ------------------------------------------------------------------
        foreach (var line in sale.LineItems)
        {
            var product = productsById[line.ProductId];
            product.DecreaseStock(line.Quantity);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "ApproveSale: sale {SaleId} ({SaleNumber}) approved by user {UserId}. " +
            "Stock decremented for {LineCount} line(s).",
            sale.Id, sale.SaleNumber, currentUser.UserId, sale.LineItems.Count);

        return Result.Success();
    }
}
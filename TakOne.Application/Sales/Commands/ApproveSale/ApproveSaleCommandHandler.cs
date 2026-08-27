using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.ApproveSale;

public sealed class ApproveSaleCommandHandler
{
    /// <summary>
    /// Maximum retry attempts for the state-transition + SaveChanges
    /// sequence. Catches <c>DbUpdateConcurrencyException</c> (sale row
    /// version conflict from a concurrent Submit / Cancel / Approve on
    /// the same sale) + SQL Server 2627/2601 unique-constraint violations.
    /// </summary>
    private const int MaxAttempts = 3;

    public static async Task<Result> HandleAsync(
        ApproveSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        ISaleStateLock saleStateLock,
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
        // ACQUIRE PER-SALE STATE-TRANSITION LOCK (race-condition fix).
        //
        // Serializes Approve against any concurrent Submit / Cancel /
        // MarkAsInvoiced on the SAME sale row. Without this, the loser of
        // a concurrent Approve × Cancel race would burn a stock decrement
        // (or a sale-row UPDATE) on a sale whose state has already moved
        // on — and the loser's SaveChangesAsync would throw
        // DbUpdateConcurrencyException, surfacing a generic error toast.
        //
        // The lock is acquired AFTER the initial sale load (we need the
        // sale's Id — but actually we have it from the command) and BEFORE
        // the pre-stock-check + Approve() mutation. The whole mutation
        // sequence below runs under the lock + inside the retry loop.
        // ------------------------------------------------------------------
        await using var _saleStateLockHandle = await saleStateLock.AcquireAsync(
            sale.Id, cancellationToken);

        // ------------------------------------------------------------------
        // Execute the state-transition + stock-decrement + SaveChanges in
        // a retry loop. Catches DbUpdateConcurrencyException (e.g. a
        // concurrent Submit on the same sale modified the row version
        // between our load and our SaveChanges) and unique-constraint
        // violations. On retry, the lambda re-loads the sale to observe
        // the new state, and the aggregate's Approve() throws
        // DomainException ("EnsurePending") if the state has moved on —
        // which we catch and return as Result.Failure (no further retry).
        // ------------------------------------------------------------------
        try
        {
            return await unitOfWork.ExecuteWithRetryAsync(
                operation: async ct =>
                {
                    // Re-load the sale fresh — a concurrent Submit / Cancel
                    // may have moved the state on between the initial load
                    // (above) and our acquisition of the lock.
                    var freshSale = await saleRepository.GetByIdWithLineItemsAsync(
                        command.SaleId, ct);
                    if (freshSale is null)
                    {
                        return Result.Failure($"Sale '{command.SaleId}' was not found.");
                    }

                    // Pre-check stock for each line. Loaded fresh on every
                    // retry attempt so a concurrent Approve on a different
                    // sale (which decremented the same product) is observed.
                    //
                    // BATCH LOAD (not N+1): collect the distinct product Ids
                    // across all line items and load them in a single
                    // GetByIdsAsync round-trip (tracked — we mutate stock
                    // below). The previous per-line GetByIdAsync loop was an
                    // N+1 costing one round-trip per distinct product.
                    var distinctProductIds = freshSale.LineItems
                        .Select(li => li.ProductId)
                        .Distinct()
                        .ToList();

                    var freshProducts = await productRepository.GetByIdsAsync(
                        distinctProductIds, ct);

                    // Build Id → Product map. If a product is missing from
                    // the batch result (hard-deleted or category-deactivated
                    // since the sale was created), surface the first affected
                    // line's snapshot ProductName in the error — same UX as
                    // before, just sourced from the batch miss instead of a
                    // per-line null check.
                    var freshProductsById = freshProducts.ToDictionary(p => p.Id);
                    foreach (var line in freshSale.LineItems)
                    {
                        if (!freshProductsById.ContainsKey(line.ProductId))
                        {
                            return Result.Failure(
                                CategoryDeactivatedErrors.Format(line.ProductName));
                        }
                    }

                    // Stock pre-check: sum quantities per product across all
                    // lines (a sale may legitimately list the same product on
                    // multiple lines) and compare against current stock.
                    foreach (var product in freshProductsById.Values)
                    {
                        var totalQuantityForProduct = freshSale.LineItems
                            .Where(li => li.ProductId == product.Id)
                            .Sum(li => li.Quantity);

                        if (totalQuantityForProduct > product.StockQuantity)
                        {
                            return Result.Failure(
                                StockErrors.Format(product.Name, product.StockQuantity, totalQuantityForProduct));
                        }
                    }

                    // Transition the sale. The aggregate's Approve() calls
                    // EnsurePending() — throws DomainException if the sale
                    // was concurrently approved/cancelled between our
                    // re-load and this call. Caught by the outer try/catch
                    // below and surfaced as a clean Result.Failure.
                    freshSale.Approve(currentUser.UserId);

                    // Decrement stock per line (single SaveChanges commits
                    // the sale status change AND the stock decrements
                    // atomically — and the notification rows created by
                    // NotifyOnSaleApprovedEventHandler too, since that
                    // handler runs in the same Wolverine outbox).
                    foreach (var line in freshSale.LineItems)
                    {
                        var product = freshProductsById[line.ProductId];
                        product.DecreaseStock(line.Quantity);
                    }

                    await unitOfWork.SaveChangesAsync(ct);

                    logger.LogInformation(
                        "ApproveSale: sale {SaleId} ({SaleNumber}) approved by user {UserId}. " +
                        "Stock decremented for {LineCount} line(s).",
                        freshSale.Id, freshSale.SaleNumber, currentUser.UserId, freshSale.LineItems.Count);

                    return Result.Success();
                },
                maxAttempts: MaxAttempts,
                cancellationToken: cancellationToken);
        }
        catch (DomainException ex)
        {
            // The aggregate's EnsurePending() threw — a concurrent
            // Submit/Approve/Cancel moved the sale state between our
            // re-load and our Approve() call. Surface as a clean failure
            // (not a retry — the state has irreversibly moved on).
            logger.LogWarning(
                "ApproveSale: domain guard failed for sale {SaleId} (likely a concurrent state transition). " +
                "Message: {Message}",
                command.SaleId, ex.Message);
            return Result.Failure(
                "The sale's state changed before approval could complete. Refresh and try again.");
        }
    }
}
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.SubmitSale;

public sealed class SubmitSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        SubmitSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        ILogger<SubmitSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        // Load with line items because the aggregate's Submit() requires
        // at least one line item and a positive total — it will inspect
        // the line items collection.
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // ------------------------------------------------------------------
        // The one forbidden thing: a sale must be submitted by its own
        // creator. Customer creates draft + employee submits = NOT allowed.
        // Employee creates on behalf + employee submits = allowed (same person).
        // ------------------------------------------------------------------
        if (sale.CreatedByUserId != currentUser.UserId)
        {
            logger.LogWarning(
                "SubmitSale: user {UserId} attempted to submit sale {SaleId} created by {CreatorId}. " +
                "Only the creator can submit a sale.",
                currentUser.UserId, sale.Id, sale.CreatedByUserId);

            return Result.Failure(
                "Only the sale's creator can submit it. " +
                "If you are a sales employee creating a sale on behalf of a customer, " +
                "submit the sale yourself from the sales-employee page.");
        }

        // ------------------------------------------------------------------
        // PURCHASE-LIMIT RE-CHECK AT SUBMIT TIME (defense-in-depth).
        //
        // The Add / Update paths already enforce the per-group purchase
        // limit at the time the line was last touched. But the limit may
        // have been LOWERED by an admin between when the user added the
        // item to their cart and when they clicked Submit. Without this
        // re-check, the user could successfully submit a sale that
        // violates the current limit — a real bug reported in the field
        // ("you CAN hit submit order and submit something more than the
        // allowed limit").
        //
        // We re-resolve the customer's GroupName from the DB (the auth-
        // cookie claim is stale — see GetActiveCartForUserQueryHandler
        // for the full rationale), look up each line's product limit for
        // that group, and reject if any line's quantity exceeds it.
        //
        // Single round-trip for the customer + single round-trip for all
        // line-item products (via GetByIdsAsync). The failure error uses
        // PurchaseLimitErrors.Format so the UI can localize it without
        // mentioning "groups".
        // ------------------------------------------------------------------
        var customer = await userRepository.GetByIdAsync(sale.CustomerId, cancellationToken);
        if (customer is null)
        {
            logger.LogError(
                "SubmitSale: customer {CustomerId} on sale {SaleId} not found.",
                sale.CustomerId, sale.Id);

            return Result.Failure(
                "The customer associated with this sale could not be found. Contact an administrator.");
        }

        // Staff (no GroupName) have no per-product cap — skip the loop.
        if (!string.IsNullOrWhiteSpace(customer.GroupName) && sale.LineItems.Count > 0)
        {
            var lineProductIds = sale.LineItems.Select(li => li.ProductId).Distinct().ToList();
            var lineProducts = await productRepository.GetByIdsAsync(lineProductIds, cancellationToken);
            var lineProductById = lineProducts.ToDictionary(p => p.Id);

            foreach (var line in sale.LineItems)
            {
                if (!lineProductById.TryGetValue(line.ProductId, out var lineProduct))
                {
                    // Defensive — the product existed when the line was
                    // added. If it's gone now, the database is in an
                    // inconsistent state. Log + fail submit.
                    logger.LogError(
                        "SubmitSale: product {ProductId} on sale {SaleId} line {LineId} no longer exists.",
                        line.ProductId, sale.Id, line.Id);

                    return Result.Failure(
                        $"Product '{line.ProductName}' no longer exists. Remove the line before submitting.");
                }

                var limitVo = lineProduct.GetPurchaseLimitForGroup(customer.GroupName);
                if (limitVo is not null && line.Quantity > limitVo.Limit)
                {
                    logger.LogWarning(
                        "SubmitSale: purchase-limit exceeded at submit time for product {ProductId} on sale {SaleId} " +
                        "(customer {CustomerId}, limit {Limit}, line qty {Qty}).",
                        line.ProductId, sale.Id, sale.CustomerId, limitVo.Limit, line.Quantity);

                    return Result.Failure(
                        PurchaseLimitErrors.Format(line.ProductName, limitVo.Limit));
                }
            }
        }

        // Delegate to the aggregate. Submit() enforces:
        //   - sale is in Draft status (throws otherwise)
        //   - sale has at least one line item (throws otherwise)
        //   - sale total is positive (throws otherwise)
        // DomainException is caught by middleware and converted to Result.Failure.
        sale.Submit();

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "SubmitSale: sale {SaleId} ({SaleNumber}) submitted by user {UserId}.",
            sale.Id, sale.SaleNumber, currentUser.UserId);

        return Result.Success();
    }
}
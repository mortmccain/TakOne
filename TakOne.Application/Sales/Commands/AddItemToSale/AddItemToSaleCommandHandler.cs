using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.AddItemToSale;

/// <summary>
/// Adds a line item to a Draft Sale. See <see cref="AddItemToSaleCommand"/> for
/// the full business rules. Not static so <c>ILogger&lt;T&gt;</c> can take it
/// as a type argument.
/// </summary>
public sealed class AddItemToSaleCommandHandler
{
    public static async Task<Result> HandleAsync(
        AddItemToSaleCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        ILogger<AddItemToSaleCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        // Load the sale WITH line items, because we need to:
        //   - check ownership
        //   - check status
        //   - find an existing line for this product (for stock aggregation)
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        // Only the creator can edit a draft. For self-buy, that's the customer.
        // For on-behalf, that's the staff member who started the draft.
        if (sale.CreatedByUserId != currentUser.UserId)
        {
            logger.LogWarning(
                "AddItemToSale: user {UserId} attempted to modify sale {SaleId} owned by {OwnerId}.",
                currentUser.UserId, sale.Id, sale.CreatedByUserId);

            return Result.Failure("You can only modify your own drafts.");
        }

        if (sale.Status != SaleStatus.Draft)
        {
            return Result.Failure(
                $"Items can only be added to a Draft sale. This sale is currently '{sale.Status}'.");
        }

        // ------------------------------------------------------------------
        // Load the product. We need it for:
        //   - name snapshot (added to the line)
        //   - price snapshot (added to the line)
        //   - stock check
        //   - per-group purchase-limit lookup
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return Result.Failure($"Product '{command.ProductId}' was not found.");
        }

        // ------------------------------------------------------------------
        // Stock check: the resulting line quantity (existing + new) must not
        // exceed current stock. If a line for this product already exists on
        // the sale, the aggregate will INCREMENT its quantity — so the final
        // quantity is what matters, not just the amount being added now.
        // ------------------------------------------------------------------
        var existingLineQuantity = sale.LineItems
            .Where(li => li.ProductId == command.ProductId)
            .Sum(li => li.Quantity);

        var resultingQuantity = existingLineQuantity + command.Quantity;

        if (resultingQuantity > product.StockQuantity)
        {
            return Result.Failure(
                $"Adding {command.Quantity} unit(s) of '{product.Name}' would bring the line total to " +
                $"{resultingQuantity}, but only {product.StockQuantity} are currently in stock.");
        }

        // ------------------------------------------------------------------
        // Purchase-limit lookup using the CUSTOMER's GroupName (not the
        // current user's, in case a staff member is editing a draft they're
        // building on behalf of a customer). The sale stores CustomerId (a
        // Guid) — we need to re-load the user to get their GroupName.
        // ------------------------------------------------------------------
        var customer = await userRepository.GetByIdAsync(sale.CustomerId, cancellationToken);
        if (customer is null)
        {
            // Defensive: the customer was loaded when the sale was created, so
            // they existed. They may have been hard-deleted in the meantime
            // (which shouldn't be possible via our domain — users are soft-
            // deleted via Deactivate()). Treat this as a data integrity issue.
            logger.LogError(
                "AddItemToSale: customer {CustomerId} on sale {SaleId} was not found in the user repository.",
                sale.CustomerId, sale.Id);

            return Result.Failure(
                "The customer associated with this sale could not be found. Contact an administrator.");
        }

        int? purchaseLimit = null;
        if (!string.IsNullOrWhiteSpace(customer.GroupName))
        {
            var limitVo = product.GetPurchaseLimitForGroup(customer.GroupName);
            purchaseLimit = limitVo?.Limit;
        }

        // ------------------------------------------------------------------
        // Add the line. The aggregate handles the limit check (throwing
        // DomainException on violation) and either creates a new line or
        // increments an existing one for the same product.
        // ------------------------------------------------------------------
        sale.AddLineItem(
            productId: product.Id,
            productName: product.Name,
            quantity: command.Quantity,
            unitPrice: product.Price,
            purchaseLimit: purchaseLimit);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "AddItemToSale: added {Qty} of product {ProductId} to sale {SaleId}. Resulting line total: {Total}.",
            command.Quantity, product.Id, sale.Id, resultingQuantity);

        return Result.Success();
    }
}
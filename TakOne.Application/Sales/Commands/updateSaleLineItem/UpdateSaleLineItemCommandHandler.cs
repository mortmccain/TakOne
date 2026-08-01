using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.UpdateSaleLineItem;

/// <summary>
/// Updates the quantity of a line item on a Draft Sale.
///
/// Re-enforces the per-group purchase limit using the line's Product, because
/// the customer's group limit (or the product's limit) may have changed since
/// the line was first added. Uses the CUSTOMER's group, not the current user's,
/// since staff may be editing a draft on behalf of a customer.
/// </summary>
public sealed class UpdateSaleLineItemCommandHandler
{
    public static async Task<Result> HandleAsync(
        UpdateSaleLineItemCommand command,
        ICurrentUserService currentUser,
        ISaleRepository saleRepository,
        IProductRepository productRepository,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        ILogger<UpdateSaleLineItemCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Result.Failure("Authentication required.");
        }

        // Need line items eagerly loaded so we can find the line by ID.
        var sale = await saleRepository.GetByIdWithLineItemsAsync(command.SaleId, cancellationToken);
        if (sale is null)
        {
            return Result.Failure($"Sale '{command.SaleId}' was not found.");
        }

        if (sale.CreatedByUserId != currentUser.UserId)
        {
            logger.LogWarning(
                "UpdateSaleLineItem: user {UserId} attempted to modify sale {SaleId} owned by {OwnerId}.",
                currentUser.UserId, sale.Id, sale.CreatedByUserId);

            return Result.Failure("You can only modify your own drafts.");
        }

        if (sale.Status != SaleStatus.Draft)
        {
            return Result.Failure(
                $"Line items can only be updated in a Draft sale. This sale is currently '{sale.Status}'.");
        }

        var lineItem = sale.LineItems.FirstOrDefault(li => li.Id == command.LineItemId);
        if (lineItem is null)
        {
            return Result.Failure(
                $"Line item '{command.LineItemId}' was not found on sale '{sale.SaleNumber}'.");
        }

        // ------------------------------------------------------------------
        // Load the product so we can:
        //   - check stock against the new quantity
        //   - re-resolve the per-group purchase limit
        //
        // READ-ONLY load (AsNoTracking): we only READ from the Product here
        // (stock + purchase limit). The mutation goes through the Sale
        // aggregate's UpdateLineItemQuantity, not through the Product.
        // Tracking the Product would put its owned Money Price into the
        // change tracker alongside the Sale's owned Money Total and the
        // SaleLineItem's owned Money UnitPrice, which can trigger
        // DbUpdateConcurrencyException at SaveChanges. See
        // CreateOrAppendSaleCommandHandler for the full rationale.
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdReadOnlyAsync(lineItem.ProductId, cancellationToken);
        if (product is null)
        {
            // Defensive — the product existed when the line was added. If it's
            // gone now, the database is in an inconsistent state.
            logger.LogError(
                "UpdateSaleLineItem: product {ProductId} on sale {SaleId} line {LineId} no longer exists.",
                lineItem.ProductId, sale.Id, lineItem.Id);

            return Result.Failure(
                $"Product '{lineItem.ProductName}' no longer exists. Remove the line instead.");
        }

        // Stock check: the new quantity must not exceed current stock.
        if (command.Quantity > product.StockQuantity)
        {
            return Result.Failure(
                $"Cannot set quantity to {command.Quantity} for '{product.Name}' — " +
                $"only {product.StockQuantity} are in stock.");
        }

        // Re-resolve the customer's group limit (may have changed since add).
        var customer = await userRepository.GetByIdAsync(sale.CustomerId, cancellationToken);
        if (customer is null)
        {
            logger.LogError(
                "UpdateSaleLineItem: customer {CustomerId} on sale {SaleId} not found.",
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
        // Delegate to the aggregate. It re-checks the purchase limit and
        // throws DomainException on violation (caught by middleware).
        // ------------------------------------------------------------------
        sale.UpdateLineItemQuantity(
            lineItemId: command.LineItemId,
            newQuantity: command.Quantity,
            purchaseLimit: purchaseLimit);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "UpdateSaleLineItem: line {LineItemId} on sale {SaleId} set to quantity {Quantity}.",
            command.LineItemId, sale.Id, command.Quantity);

        return Result.Success();
    }
}
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Sales.Commands.CreateOrAppendSale;

/// <summary>
/// Handler for <see cref="CreateOrAppendSaleCommand"/>. Implements the
/// "Add to cart" self-buy flow:
///
/// <list type="number">
///   <item>Load the Product (for name, price, stock check, purchase limit).</item>
///   <item>Stock check: resulting line quantity ≤ product.StockQuantity.</item>
///   <item>Look up the current user's per-group purchase limit (if any).</item>
///   <item>Find the user's active Draft Sale via
///         <see cref="ISaleRepository.GetActiveDraftForUserAsync"/>.</item>
///   <item>
///     <b>If found</b>: call <c>sale.AddLineItem(...)</c> — the aggregate
///     either creates a new line or increments an existing one for this
///     product (the aggregate enforces the purchase limit and recalculates
///     the sale Total).
///   </item>
///   <item>
///     <b>If not found</b>: generate a fresh SaleNumber, create a new Sale
///     via <c>Sale.Create(...)</c> with the current user as both customer
///     and creator, add the line, then register it with the repository.
///   </item>
///   <item>SaveChanges via UnitOfWork (single transaction).</item>
/// </list>
///
/// WHY NO EXPLICIT TRANSACTION SCOPE:
///   Wolverine's <c>AutoApplyTransactions</c> policy (configured in
///   <c>AddTakOneInfrastructure</c>) wraps every handler that touches a
///   DbContext in an EF Core transaction. If anything throws, the
///   transaction rolls back — no orphan Sale rows, no orphan LineItem rows.
///
/// WHY THE RESULT RETURNS THE SALE ID:
///   The UI uses it to optionally navigate to <c>/Cart</c> (Phase 4.1)
///   after a successful add. We don't return the full Sale DTO because
///   the UI doesn't need it — the cart page will re-query for the full
///   draft with line items when it loads.
/// </summary>
public sealed class CreateOrAppendSaleCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        CreateOrAppendSaleCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        ISaleRepository saleRepository,
        ISaleNumberGenerator saleNumberGenerator,
        IUnitOfWork unitOfWork,
        ILogger<CreateOrAppendSaleCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check. [RequireRoles] middleware already
        //    rejects unauthenticated callers, but this method may also be
        //    invoked in tests or from a non-HTTP host — re-checking here
        //    keeps the invariant honest.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the product. We need it for:
        //      - name snapshot (added to the line)
        //      - price snapshot (added to the line)
        //      - stock check
        //      - per-group purchase-limit lookup
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            logger.LogWarning
                ("CreateOrAppendSale: product {ProductId} not found. Requested by user {UserId}.",
                command.ProductId, currentUser.UserId);

            return Result<Guid>.Failure($"Product '{command.ProductId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Find the user's active draft (if any). Returns a TRACKED Sale
        //    with LineItems already eager-loaded — we can call AddLineItem
        //    directly on it without a re-query.
        // ------------------------------------------------------------------
        var sale = await saleRepository.GetActiveDraftForUserAsync(currentUser.UserId, cancellationToken);

        // ------------------------------------------------------------------
        // 3. Stock check. The aggregate's AddLineItem either creates a new
        //    line OR increments an existing one for this product. So the
        //    "resulting" quantity we need to validate against stock is:
        //      existing-line-quantity + command.Quantity
        //    (NOT just command.Quantity). If no sale exists yet, existing
        //    is 0.
        // ------------------------------------------------------------------
        var existingLineQuantity = sale?.LineItems
            .Where(li => li.ProductId == command.ProductId)
            .Sum(li => li.Quantity) ?? 0;

        var resultingQuantity = existingLineQuantity + command.Quantity;

        if (resultingQuantity > product.StockQuantity)
        {
            logger.LogWarning
                ("CreateOrAppendSale: stock check failed for product {ProductId}. " + "Requested {Qty}, existing in cart {ExistingQty}, stock {StockQty}.",
                product.Id, command.Quantity, existingLineQuantity, product.StockQuantity);

            return Result<Guid>.Failure
                ($"Adding {command.Quantity} unit(s) of '{product.Name}' would bring your cart total to " +
                $"{resultingQuantity}, but only {product.StockQuantity} are currently in stock.");
        }

        // ------------------------------------------------------------------
        // 4. Purchase-limit lookup using the CURRENT USER's GroupName.
        //    For staff (Employee/Manager/Admin), GroupName is null and no
        //    limit applies — they can buy as much as stock allows.
        //    For customers, GroupName comes from their User.GroupName (set
        //    via AssignUserToGroup command). We look up the matching
        //    CustomerGroupPurchaseLimit on this product.
        // ------------------------------------------------------------------
        int? purchaseLimit = null;

        if (!string.IsNullOrWhiteSpace(currentUser.GroupName))
        {
            var limitVo = product.GetPurchaseLimitForGroup(currentUser.GroupName);
            purchaseLimit = limitVo?.Limit;
        }

        // ------------------------------------------------------------------
        // 5a. APPEND path — sale exists, add the line.
        // ------------------------------------------------------------------
        if (sale is not null)
        {
            // The aggregate enforces the purchase limit (throws DomainException
            // on violation) and either creates a new line or increments an
            // existing one for the same product.
            sale.AddLineItem
                (
                productId: product.Id,
                productName: product.Name,
                quantity: command.Quantity,
                unitPrice: product.Price,
                purchaseLimit: purchaseLimit
                );

            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogInformation
                ("CreateOrAppendSale: appended {Qty} of product {ProductId} to existing draft {SaleId}. Resulting line total: {Total}.",
                command.Quantity, product.Id, sale.Id, resultingQuantity);

            return Result<Guid>.Success(sale.Id);
        }

        // ------------------------------------------------------------------
        // 5b. CREATE path — no active draft, start a fresh one.
        // ------------------------------------------------------------------
        var saleNumber = await saleNumberGenerator.NextAsync(cancellationToken);

        sale = Sale.Create
            (
            customerId: currentUser.UserId,
            customerName: currentUser.FullName,
            saleNumber: saleNumber,
            createdByUserId: currentUser.UserId,
            createdByName: currentUser.FullName
            );

        sale.AddLineItem
            (
            productId: product.Id,
            productName: product.Name,
            quantity: command.Quantity,
            unitPrice: product.Price,
            purchaseLimit: purchaseLimit
            );

        await saleRepository.AddAsync(sale, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("CreateOrAppendSale: created new draft {SaleId} ({SaleNumber}) for user {UserId} with {Qty} of product {ProductId}.",
            sale.Id, sale.SaleNumber, currentUser.UserId, command.Quantity, product.Id);

        return Result<Guid>.Success(sale.Id);
    }
}
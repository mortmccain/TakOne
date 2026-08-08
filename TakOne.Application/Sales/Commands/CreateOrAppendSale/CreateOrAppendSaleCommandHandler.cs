using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

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
///
/// CONCURRENCY / RETRY STRATEGY (added to fix the "double-add-to-cart" race):
///   The entire find-or-create + add-line + SaveChanges sequence is wrapped
///   in <see cref="IUnitOfWork.ExecuteWithRetryAsync{T}"/>. The retry
///   catches <c>DbUpdateConcurrencyException</c> and SQL Server
///   unique-constraint violations (errors 2601 and 2627), clears the
///   change tracker, and re-runs the WHOLE sequence from scratch.
///
///   This is required because the UI can fire multiple
///   <c>CreateOrAppendSaleCommand</c> invocations in parallel (rapid
///   clicks, multi-tab users, refresh-during-add). All concurrent
///   invocations start by loading the user's draft Sale (which has no
///   line for this product yet), all compute <c>LineNumber = 1</c> for
///   the new line, and all INSERT. The <c>(SaleId, LineNumber)</c>
///   unique index lets only one INSERT win; the rest fail. The retry
///   resolves this naturally: on retry, the losing invocation re-loads
///   the now-modified Sale (which contains the winning INSERT as a line
///   for this product), and the aggregate's <c>AddLineItem</c> takes the
///   "existing line" branch — incrementing the existing line's Quantity
///   instead of attempting another duplicate INSERT.
///
///   The lambda passed to <c>ExecuteWithRetryAsync</c> is IDEMPOTENT
///   under re-execution: it re-queries the draft on every attempt, so
///   concurrent commits made by sibling invocations are observed on the
///   next retry. Business-rule failures (stock check, purchase limit)
///   are RETURNED as <c>Result.Failure</c>, not thrown — they don't
///   trigger retry.
/// </summary>
public sealed class CreateOrAppendSaleCommandHandler
{
    /// <summary>
    /// Maximum number of attempts the retry loop will make before giving
    /// up. Tuned for the "double-add-to-cart" race: with 3 attempts and
    /// linear 50/100ms backoff, the worst-case latency added by retries
    /// is 150ms — well under the human-perceptible threshold for a cart
    /// interaction. If we ever see this exhausted in production logs,
    /// investigate (it would indicate either extreme contention or a
    /// non-transient bug in the aggregate's mutation logic).
    /// </summary>
    private const int MaxAttempts = 3;

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
        //
        // READ-ONLY LOAD (AsNoTracking): we never mutate the Product in this
        // handler — we only read its data to snapshot into the SaleLineItem.
        // Loading it AsNoTracking keeps the Product (and its complex Money
        // Price, and its owned PurchaseLimits collection) OUT of the change
        // tracker entirely. This is a defensive measure to keep the change
        // tracker's working set small and avoid any chance of accidental
        // Product mutations being persisted.
        //
        // HISTORICAL CONTEXT (now resolved at the EF mapping level):
        //   Money was previously mapped as OwnsOne on three entities:
        //     Product.Price#Money, SaleLineItem.UnitPrice#Money, Sale.Total#Money
        //   OwnsOne tracks by reference identity, and replacing a Money
        //   reference (e.g. Sale.RecalculateTotal's `Total = newMoney`)
        //   produced DbUpdateConcurrencyException: "expected to affect 1
        //   row(s), but actually affected 0 row(s)". The AsNoTracking +
        //   defensive copy pattern below was a workaround for that bug.
        //
        //   Money is now mapped as ComplexProperty (EF Core 9+) on all three
        //   entities. ComplexProperty has VALUE semantics — EF compares by
        //   value, not reference identity — so reference replacement works
        //   correctly. The AsNoTracking + defensive copy pattern is kept
        //   here as DDD best practice (value objects shouldn't be shared
        //   across aggregate boundaries), but it is no longer load-bearing
        //   for the EF tracking behavior.
        //
        // The product load is OUTSIDE the retry loop because:
        //   - It never conflicts (AsNoTracking read).
        //   - It never changes between retries (Products don't mutate while
        //     the user is adding to cart).
        //   - Re-loading it would just add latency.
        //
        // The new Money(amount, currency) snapshot below still applies —
        // it guarantees the SaleLineItem's UnitPrice is a fresh instance,
        // not a reference to the (now-untracked) Product.Price.
        // ------------------------------------------------------------------
        var product = await productRepository.GetByIdReadOnlyAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            logger.LogWarning
                ("CreateOrAppendSale: product {ProductId} not found. Requested by user {UserId}.",
                command.ProductId, currentUser.UserId);

            return Result<Guid>.Failure($"Product '{command.ProductId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Purchase-limit lookup using the CURRENT USER's GroupName.
        //    For staff (Employee/Manager/Admin), GroupName is null and no
        //    limit applies — they can buy as much as stock allows.
        //    For customers, GroupName comes from their User.GroupName (set
        //    via AssignUserToGroup command). We look up the matching
        //    CustomerGroupPurchaseLimit on this product.
        //
        //    This is OUTSIDE the retry loop because:
        //      - It depends only on the product (read above, immutable for
        //        the handler's lifetime) and the user's GroupName (also
        //        immutable for the handler's lifetime).
        //      - Re-querying it on each retry would be wasted work.
        // ------------------------------------------------------------------
        int? purchaseLimit = null;
        if (!string.IsNullOrWhiteSpace(currentUser.GroupName))
        {
            var limitVo = product.GetPurchaseLimitForGroup(currentUser.GroupName);
            purchaseLimit = limitVo?.Limit;
        }

        // ------------------------------------------------------------------
        // 3. Snapshot the unit price ONCE, outside the retry loop.
        //
        //    SaleLineItem's constructor makes its OWN defensive copy of
        //    the Money instance (see SaleLineItem.cs), so passing the same
        //    `unitPrice` reference into multiple AddLineItem calls (one per
        //    retry attempt) is safe — each call produces an independent
        //    tracked Money instance via `new Money(unitPrice.Amount,
        //    unitPrice.Currency)`.
        //
        //    We snapshot it here (rather than passing product.Price directly
        //    into AddLineItem) so that the retry loop never re-touches the
        //    Product — the retry's lambda has no closure over `product.Price`,
        //    only over the snapshot value.
        // ------------------------------------------------------------------
        var unitPrice = new Money(product.Price.Amount, product.Price.Currency);

        // ------------------------------------------------------------------
        // 4. Find-or-create + add-line + SaveChanges, wrapped in a retry
        //    loop. The lambda is idempotent under re-execution: it re-queries
        //    the active draft on every attempt, so concurrent invocations
        //    that committed before us are seen on retry. Business-rule
        //    failures (stock check, purchase limit) are RETURNED as
        //    Result.Failure, not thrown — they don't trigger retry.
        // ------------------------------------------------------------------
        return await unitOfWork.ExecuteWithRetryAsync(
            operation: async ct =>
            {
                // --------------------------------------------------------------
                // 4a. Find the user's active draft (if any). Returns a TRACKED
                //     Sale with LineItems already eager-loaded — we can call
                //     AddLineItem directly on it without a re-query.
                //
                //     This re-runs on EVERY retry attempt. After a losing
                //     race, the retry sees the winning invocation's
                //     committed INSERT as an existing line for this product,
                //     and Sale.AddLineItem takes the "increment existing
                //     line's quantity" branch instead of attempting another
                //     duplicate INSERT.
                // --------------------------------------------------------------
                var sale = await saleRepository.GetActiveDraftForUserAsync(currentUser.UserId, ct);

                // --------------------------------------------------------------
                // 4b. Stock check. The aggregate's AddLineItem either
                //     creates a new line OR increments an existing one for
                //     this product. So the "resulting" quantity we need to
                //     validate against stock is:
                //       existing-line-quantity + command.Quantity
                //     (NOT just command.Quantity). If no sale exists yet,
                //     existing is 0.
                //
                //     This re-runs on EVERY retry attempt — the existing
                //     line quantity can change between attempts when a
                //     concurrent invocation wins the race.
                // --------------------------------------------------------------
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

                // --------------------------------------------------------------
                // 4c. APPEND path — sale exists, add the line.
                //
                //     The aggregate enforces the purchase limit (throws
                //     DomainException on violation) and either creates a new
                //     line or increments an existing one for the same product.
                // --------------------------------------------------------------
                if (sale is not null)
                {
                    sale.AddLineItem
                        (
                        productId: product.Id,
                        productName: product.Name,
                        quantity: command.Quantity,
                        unitPrice: unitPrice,
                        purchaseLimit: purchaseLimit
                        );

                    await unitOfWork.SaveChangesAsync(ct);

                    logger.LogInformation
                        ("CreateOrAppendSale: appended {Qty} of product {ProductId} to existing draft {SaleId}. Resulting line total: {Total}.",
                        command.Quantity, product.Id, sale.Id, resultingQuantity);

                    return Result<Guid>.Success(sale.Id);
                }

                // --------------------------------------------------------------
                // 4d. CREATE path — no active draft, start a fresh one.
                //
                //     This path can ALSO race: two concurrent invocations
                //     both see `sale == null`, both call SaleNumberGenerator
                //     (which has its own concurrency guard via the
                //     (SaleNumber_Year, SaleNumber_Sequence) unique index),
                //     both create a new Sale, and both SaveChanges. The
                //     loser's SaveChanges fails with a unique-constraint
                //     violation on the SaleNumber index — and the retry
                //     loop catches it.
                //
                //     On retry, the losing invocation re-runs
                //     GetActiveDraftForUserAsync — which now sees the winning
                //     invocation's committed Sale (the most-recent Draft for
                //     this user) and falls through to the APPEND path (4c)
                //     above. So the user ends up with ONE draft Sale
                //     containing the line, not two orphan drafts.
                // --------------------------------------------------------------
                var saleNumber = await saleNumberGenerator.NextAsync(ct);

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
                    unitPrice: unitPrice,
                    purchaseLimit: purchaseLimit
                    );

                await saleRepository.AddAsync(sale, ct);
                await unitOfWork.SaveChangesAsync(ct);

                logger.LogInformation
                    ("CreateOrAppendSale: created new draft {SaleId} ({SaleNumber}) for user {UserId} with {Qty} of product {ProductId}.",
                    sale.Id, sale.SaleNumber, currentUser.UserId, command.Quantity, product.Id);

                return Result<Guid>.Success(sale.Id);
            },
            maxAttempts: MaxAttempts,
            cancellationToken: cancellationToken);
    }
}
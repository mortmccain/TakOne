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
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        ISaleNumberGenerator saleNumberGenerator,
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
        // CATEGORY-DEACTIVATION RE-CHECK (also defense-in-depth):
        //   Even if every line passed Add/Update validation, an admin may
        //   have DEACTIVATED the product's Category / SubCategory /
        //   SubSubCategory between the time the line was added and the
        //   time the user clicked Submit. Such products must NOT be
        //   submittable — but their StockQuantity is preserved (the
        //   admin's intent was to suppress visibility, not to delete
        //   inventory). We reject with CategoryDeactivatedErrors.Format
        //   so the UI can localize the message and the user knows which
        //   product to remove from their cart.
        //
        // Single round-trip for the customer + single round-trip for all
        // line-item products (via GetByIdsAsync). The failure errors use
        // PurchaseLimitErrors.Format / CategoryDeactivatedErrors.Format
        // so the UI can localize them without mentioning "groups".
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

        if (sale.LineItems.Count > 0)
        {
            var lineProductIds = sale.LineItems.Select(li => li.ProductId).Distinct().ToList();
            var lineProducts = await productRepository.GetByIdsAsync(lineProductIds, cancellationToken);
            var lineProductById = lineProducts.ToDictionary(p => p.Id);

            // ---- Pass 1: category-deactivation check (applies to ALL
            //              customers, including staff with no GroupName).
            //              A single product whose category was deactivated
            //              fails the entire submit — the user must remove
            //              the line or ask an admin to reactivate the
            //              category.
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

                var hierarchyActive = await categoryRepository.IsProductCategoryHierarchyActiveAsync
                    (
                    lineProduct.CategoryId,
                    lineProduct.SubCategoryId,
                    lineProduct.SubSubCategoryId,
                    cancellationToken
                    );

                if (!hierarchyActive)
                {
                    logger.LogWarning(
                        "SubmitSale: product {ProductId} ('{ProductName}') on sale {SaleId} line {LineId} is under a deactivated category " +
                        "(Category={CategoryId}, Sub={SubCategoryId}, SubSub={SubSubCategoryId}). Rejecting submit.",
                        lineProduct.Id, lineProduct.Name, sale.Id, line.Id,
                        lineProduct.CategoryId, lineProduct.SubCategoryId, lineProduct.SubSubCategoryId);

                    return Result.Failure(
                        CategoryDeactivatedErrors.Format(lineProduct.Name));
                }
            }

            // ---- Pass 2: purchase-limit check (ONLY for customers with
            //              a GroupName — staff have no per-product cap).
            if (!string.IsNullOrWhiteSpace(customer.GroupName))
            {
                foreach (var line in sale.LineItems)
                {
                    // lineProductById was populated above; the TryGetValue
                    // already succeeded in Pass 1, so it will succeed here
                    // too. Defensive null-check kept for safety.
                    if (!lineProductById.TryGetValue(line.ProductId, out var lineProduct))
                    {
                        continue;
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
        }

        // ------------------------------------------------------------------
        // EMPTY-CART GUARD (defense-in-depth).
        //
        // The Sale aggregate's Submit() method throws DomainException if
        // the sale has no line items — which is caught by middleware and
        // converted to Result.Failure with an English message
        // ("Cannot submit a sale with no line items."). That English
        // message leaks to the UI even in Persian mode.
        //
        // More importantly, this guard is the LAST line of defense against
        // the "empty order" bug: if a ghost draft (an empty Draft row from
        // a buggy code path — see QuickReorderLastSaleCommandHandler's
        // commentary on the ghost-draft bug) ever makes it to the Submit
        // flow, this check blocks it from becoming a Pending order. The
        // user's cart page ALSO guards against this (the Submit button is
        // disabled when the cart is empty, and HasStockIssue / HasLimitIssue
        // re-check in the handler), but defense-in-depth means we don't
        // trust any single layer.
        //
        // Returns a stable, culture-neutral error code that the UI
        // localizes — mirroring the PurchaseLimitErrors / StockErrors /
        // CategoryDeactivatedErrors pattern.
        //
        // NOTE: This guard runs BEFORE sale-number allocation. An empty
        // cart must NOT burn a permanent sequence number — the allocation
        // below costs a row update in SaleSequenceCounters and (under the
        // gap-burning semantics of the generator) cannot be rolled back.
        // Blocking empty carts here keeps the permanent sequence clean.
        // ------------------------------------------------------------------
        if (sale.LineItems.Count == 0)
        {
            logger.LogWarning
                ("SubmitSale: sale {SaleId} has 0 line items. " +
                 "Blocking submit — an empty draft should never reach the submit flow " +
                 "(the QuickReorder handler's compute-first architecture prevents this, " +
                 "and the Cart page disables the Submit button for empty carts). " +
                 "No sale number was allocated.",
                sale.Id);

            return Result.Failure("SubmitEmptyCart");
        }

        // ------------------------------------------------------------------
        // SALE NUMBER ALLOCATION (B2 deferred-allocation design).
        //
        // The permanent SaleNumber is allocated HERE — at submit time, NOT
        // at draft creation. Drafts are disposable carts; many are created
        // and abandoned. Allocating at draft creation would burn permanent
        // sequence numbers on drafts that never get submitted, producing
        // gaps in the audit trail of POSTED sales.
        //
        // By allocating here, the permanent sequence stays clean: gaps only
        // come from voided/cancelled submitted sales, which is the correct,
        // auditable ERP behavior (matching SAP, Oracle, NetSuite).
        //
        // The generator (SaleNumberGenerator) uses an atomic UPDATE...OUTPUT
        // on the SaleSequenceCounters table — the allocation commits
        // independently of Wolverine's ambient transaction, so the number
        // is "burned" even if SaveChangesAsync later fails. This is
        // intentional: it prevents two concurrent submits from getting the
        // same number, and the gap (if SaveChanges fails) is acceptable
        // and auditable.
        //
        // We allocate AFTER all validation passes (empty-cart guard,
        // purchase-limit re-check, category-deactivation re-check) so that
        // a rejected submit does NOT burn a number.
        // ------------------------------------------------------------------
        var saleNumber = await saleNumberGenerator.NextAsync(cancellationToken);

        // Delegate to the aggregate. Submit() enforces:
        //   - sale is in Draft status (throws otherwise)
        //   - sale has at least one line item (throws otherwise — redundant
        //     with the guard above, but the domain doesn't trust the
        //     application layer either)
        //   - sale total is positive (throws otherwise)
        //   - saleNumber is non-null (throws otherwise — programmer error)
        // Submit() assigns the SaleNumber to the Sale aggregate (it was null
        // while the sale was a Draft).
        // DomainException is caught by middleware and converted to Result.Failure.
        sale.Submit(saleNumber);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "SubmitSale: sale {SaleId} ({SaleNumber}) submitted by user {UserId}.",
            sale.Id, sale.SaleNumber, currentUser.UserId);

        return Result.Success();
    }
}
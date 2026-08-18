using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Products.ValueObjects;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Customers.Commands.BulkApplyDefaultsForGroup;

/// <summary>
/// Handler for <see cref="BulkApplyDefaultsForGroupCommand"/>.
///
/// Closes the "reactivation gap" documented since Step 5:
///   When a group is deactivated and new products are created during the
///   inactive period, those new products do NOT get default purchase-limit
///   rows for the inactive group (CreateProduct Phase-1 auto-default loop
///   only iterates ACTIVE groups). On reactivation, the group's customers
///   have NO purchase cap (null = unlimited) on those new products until
///   the admin manually sets limits via Manage Products.
///
/// This handler fills that gap by scanning every product in the catalog
/// and inserting a default limit row (= <see cref="CustomerGroupPurchaseLimit.DefaultLimit"/>)
/// wherever one is missing for the given group. Products that already have
/// a limit row (whether from the original CreateGroup bulk-default flow,
/// from an explicit SetProductPurchaseLimit, or from CreateProduct Phase-1)
/// are SKIPPED — admin overrides are preserved.
///
/// BATCHING:
///   Mirrors <c>CreateCustomerGroupCommandHandler</c>'s bulk-default loop:
///   - <see cref="IProductRepository.GetAllProductIdsAsync"/>: single
///     round-trip, lightweight (just IDs).
///   - Loop in batches of <see cref="BulkBatchSize"/> (200): load tracked
///     products via <see cref="IProductRepository.GetByIdsAsync"/>, check
///     each via <see cref="Product.GetPurchaseLimitForGroup"/>, set the
///     default only where missing, SaveChanges, ClearChangeTracker.
///   - Each batch is one SaveChanges round-trip → bounded memory regardless
///     of catalog size.
///
/// IDEMPOTENCY:
///   Fully idempotent. Re-running on a fully-defaulted catalog is a no-op
///   (every product already has a limit row → ProductsUpdated = 0).
///
/// TRANSACTION SAFETY:
///   All batches run in the ambient Wolverine transaction. If any batch
///   fails, the entire operation rolls back — no partial state. The
///   intermediate SaveChanges calls commit WITHIN the ambient transaction
///   (visible to subsequent reads in this same DbContext, invisible to
///   other requests until the ambient commits at handler exit).
/// </summary>
public sealed class BulkApplyDefaultsForGroupCommandHandler
{
    /// <summary>
    /// Number of products to load + inspect per SaveChanges round.
    /// 200 is the same batch size used by CreateCustomerGroupCommandHandler
    /// — tuned for SQL Server's sweet spot (~600 change-tracker entries
    /// per batch in the worst case where every product gets a new limit).
    /// </summary>
    private const int BulkBatchSize = 200;

    public static async Task<Result<BulkApplyDefaultsResult>> HandleAsync(
        BulkApplyDefaultsForGroupCommand command,
        ICurrentUserService currentUser,
        ICustomerGroupRepository customerGroupRepository,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<BulkApplyDefaultsForGroupCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check. [RequireRoles] already rejected
        //    unauthorized callers via AuthorizationMiddleware, but this
        //    handler may also be invoked from tests or a non-HTTP host.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("BulkApplyDefaultsForGroup: unauthenticated call rejected.");
            return Result<BulkApplyDefaultsResult>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Load the group. We need it to:
        //    (a) verify existence (friendly error if not found);
        //    (b) verify the group is ACTIVE (the bulk-default-on-reactivated
        //        flow only makes sense for active groups — see command XML doc);
        //    (c) read the group Name for log messages.
        //
        //    Tracked load is fine — we don't mutate the group itself, but
        //    tracking it doesn't hurt (one extra row in the change tracker
        //    that won't generate any UPDATE on SaveChanges because we never
        //    touch it). The alternative (GetByIdReadOnlyAsync) is also
        //    acceptable; we use tracked here for consistency with
        //    ActivateCustomerGroupCommandHandler.
        // ------------------------------------------------------------------
        var group = await customerGroupRepository.GetByIdAsync(command.GroupId, cancellationToken);
        if (group is null)
        {
            logger.LogWarning(
                "BulkApplyDefaultsForGroup: group {GroupId} not found. Requested by user {UserId}.",
                command.GroupId, currentUser.UserId);
            return Result<BulkApplyDefaultsResult>.Failure(
                $"Customer group '{command.GroupId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Enforce "group must be active".
        //    The use case is "reactivate first, then fill the gap". Running
        //    this command on an inactive group would insert limit rows for
        //    a group whose customers can't purchase from anyway — a
        //    confusing state. The admin should Activate the group first,
        //    then run this command. Surface as a friendly Result.Failure
        //    (not a DomainException — this is an application-layer rule,
        //    not a domain invariant).
        // ------------------------------------------------------------------
        if (!group.IsActive)
        {
            logger.LogWarning(
                "BulkApplyDefaultsForGroup: group {GroupId} ('{GroupName}') is inactive. User {UserId} rejected — activate the group first.",
                group.Id, group.Name, currentUser.UserId);
            return Result<BulkApplyDefaultsResult>.Failure(
                $"Customer group '{group.Name}' is inactive. Activate the group first, then run bulk-apply-defaults.");
        }

        // ------------------------------------------------------------------
        // 3. Snapshot all existing product IDs. Single round-trip,
        //    lightweight (no entity materialization, no tracking). The
        //    snapshot is taken AFTER the group-active check so we don't
        //    waste a round-trip on a request that's about to be rejected.
        // ------------------------------------------------------------------
        var allProductIds = await productRepository.GetAllProductIdsAsync(cancellationToken);

        var productsUpdated = 0;
        var productsSkipped = 0;

        logger.LogInformation(
            "BulkApplyDefaultsForGroup: beginning scan of {ProductCount} products for group {GroupId} ('{GroupName}'). Default limit = {DefaultLimit}. Requested by user {UserId}.",
            allProductIds.Count, group.Id, group.Name,
            CustomerGroupPurchaseLimit.DefaultLimit, currentUser.UserId);

        // ------------------------------------------------------------------
        // 4. BATCHED BULK-DEFAULT LOOP.
        //    For each batch of BulkBatchSize product IDs:
        //      a. Load tracked products via GetByIdsAsync (single round-trip
        //         per batch). Tracked because we need EF Core to detect the
        //         new CustomerGroupPurchaseLimit entries added by
        //         Product.SetPurchaseLimit and generate INSERTs on SaveChanges.
        //      b. For each product, check Product.GetPurchaseLimitForGroup:
        //           - if the product ALREADY has a limit for this group,
        //             SKIP it (preserve admin-set values, including
        //             overrides from SetProductPurchaseLimit or from
        //             CreateProduct Phase-2 user overrides).
        //           - if NO limit exists, call SetPurchaseLimit with the
        //             DefaultLimit.
        //      c. SaveChanges (commit the batch within the ambient
        //         Wolverine transaction).
        //      d. ClearChangeTracker (free memory before the next batch —
        //         important for large catalogs to avoid 10K+ tracked
        //         entities accumulating).
        // ------------------------------------------------------------------
        for (int i = 0; i < allProductIds.Count; i += BulkBatchSize)
        {
            var batchIds = allProductIds.Skip(i).Take(BulkBatchSize).ToList();

            var batchProducts = await productRepository.GetByIdsAsync(batchIds, cancellationToken);

            var batchUpdated = 0;
            var batchSkipped = 0;

            foreach (var product in batchProducts)
            {
                // GetPurchaseLimitForGroup returns null if no limit exists
                // for the given group on this product. We use it (rather
                // than calling SetPurchaseLimit unconditionally) so we
                // PRESERVE admin-set values — SetPurchaseLimit REPLACES
                // any existing entry for the same group, which would blow
                // away admin overrides.
                var existingLimit = product.GetPurchaseLimitForGroup(group.Id);
                if (existingLimit is not null)
                {
                    batchSkipped++;
                    continue;
                }

                product.SetPurchaseLimit(group.Id, CustomerGroupPurchaseLimit.DefaultLimit);
                batchUpdated++;
            }

            // Only SaveChanges if at least one product in the batch got a
            // new limit. If the entire batch was skipped (all products
            // already had limits), skip the SaveChanges round-trip too —
            // small optimization for the idempotent re-run case.
            if (batchUpdated > 0)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            // Clear the change tracker between batches regardless of
            // whether we saved — even read-only tracked entities from
            // GetByIdsAsync accumulate, and clearing them keeps memory
            // bounded for large catalogs.
            unitOfWork.ClearChangeTracker();

            productsUpdated += batchUpdated;
            productsSkipped += batchSkipped;

            logger.LogInformation(
                "BulkApplyDefaultsForGroup: batch {BatchStart}-{BatchEnd} for group {GroupId} — updated {BatchUpdated}, skipped {BatchSkipped}. Cumulative: {CumulativeUpdated}/{CumulativeTotal} updated, {CumulativeSkipped}/{CumulativeTotal} skipped.",
                i + 1, i + batchProducts.Count, group.Id,
                batchUpdated, batchSkipped,
                productsUpdated, allProductIds.Count,
                productsSkipped, allProductIds.Count);
        }

        logger.LogInformation(
            "BulkApplyDefaultsForGroup: complete. Group {GroupId} ('{GroupName}') — {ProductsUpdated} products received default limit {DefaultLimit}, {ProductsSkipped} already had limits and were skipped. Total scanned: {TotalScanned}. Requested by user {UserId}.",
            group.Id, group.Name,
            productsUpdated, CustomerGroupPurchaseLimit.DefaultLimit,
            productsSkipped, allProductIds.Count, currentUser.UserId);

        return Result<BulkApplyDefaultsResult>.Success(new BulkApplyDefaultsResult
        {
            TotalProductsScanned = allProductIds.Count,
            ProductsUpdated = productsUpdated,
            ProductsSkipped = productsSkipped,
            AppliedLimit = CustomerGroupPurchaseLimit.DefaultLimit
        });
    }
}
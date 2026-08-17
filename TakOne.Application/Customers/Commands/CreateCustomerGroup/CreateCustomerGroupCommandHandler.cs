using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Products.ValueObjects;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Customers.Commands.CreateCustomerGroup;

/// <summary>
/// Handler for <see cref="CreateCustomerGroupCommand"/>.
///
/// Validates name uniqueness at the application layer (friendly error
/// before hitting the DB unique index), constructs the Money value
/// object from the DTO, delegates aggregate creation to
/// <c>CustomerGroup.Create</c>, and THEN bulk-applies the default
/// per-group purchase limit (=
/// <see cref="CustomerGroupPurchaseLimit.DefaultLimit"/>) to EVERY
/// existing product in the catalog (Step 5 wiring).
///
/// WHY BULK-APPLY DEFAULT LIMITS:
///   When a new CustomerGroup is created, every existing product
///   should immediately have a per-group limit for it — otherwise
///   the new group's customers would have NO purchase cap (null =
///   unlimited) until the admin manually sets limits per product.
///   The default limit (1) is the safest baseline; admins can
///   override individual limits via the Manage Products page.
///
/// ATOMICITY:
///   The bulk-default loop runs in the SAME Wolverine ambient
///   transaction as the group creation. If any batch fails, the
///   entire operation rolls back — no orphan group row, no orphan
///   limit rows. The group only becomes visible to other requests
///   when the ambient transaction commits at handler exit.
/// </summary>
public sealed class CreateCustomerGroupCommandHandler
{
    /// <summary>
    /// Number of products to load + mutate per SaveChanges round.
    /// 200 is tuned for SQL Server's sweet spot: ~600 change-tracker
    /// entries per batch (200 products × ~3 owned value objects each),
    /// comfortably under EF Core's practical limits. Each batch is one
    /// SaveChanges call → one round-trip to the DB.
    /// </summary>
    private const int BulkBatchSize = 200;

    public static async Task<Result<Guid>> HandleAsync(
        CreateCustomerGroupCommand command,
        ICurrentUserService currentUser,
        ICustomerGroupRepository customerGroupRepository,
        IProductRepository productRepository,
        IUnitOfWork unitOfWork,
        ILogger<CreateCustomerGroupCommandHandler> logger,
        CancellationToken cancellationToken)
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check. [RequireRoles] already rejected
        //    unauthorized callers, but this handler may also be invoked
        //    from tests or a non-HTTP host.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateCustomerGroup: unauthenticated call rejected.");
            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Name uniqueness. Friendly pre-check; the DB unique index on
        //    CustomerGroups.Name is the hard guarantee against concurrent
        //    races between our check and our SaveChanges.
        // ------------------------------------------------------------------
        var nameExists = await customerGroupRepository.NameExistsAsync(
            command.Name, excludeId: null, cancellationToken);

        if (nameExists)
        {
            logger.LogWarning(
                "CreateCustomerGroup: name '{Name}' already exists. User {UserId} rejected.",
                command.Name, currentUser.UserId);

            return Result<Guid>.Failure(
                $"A customer group with the name '{command.Name}' already exists. Choose a different name.");
        }

        // ------------------------------------------------------------------
        // 2. Construct the Money value object. The Money constructor
        //    throws DomainException on invalid input (wrong-length currency,
        //    negative amount) — caught by middleware.
        // ------------------------------------------------------------------
        var salary = new Money(command.SalaryAmount, command.SalaryCurrency);

        // ------------------------------------------------------------------
        // 3. Create the aggregate. CustomerGroup.Create enforces the
        //    domain invariants (Name 1..100, Salary > 0, currency 3-letter).
        // ------------------------------------------------------------------
        var group = Domain.Customers.Entities.CustomerGroup.Create(command.Name, salary);

        // ------------------------------------------------------------------
        // 4. Persist the new group. ICustomerGroupRepository.AddAsync
        //    tracks the group; we SaveChanges now so the group row is
        //    visible to subsequent Product loads in the bulk-default loop
        //    below (the FK constraint on ProductPurchaseLimits.GroupId
        //    requires the group row to exist).
        //
        //    SaveChanges here commits WITHIN the ambient Wolverine
        //    transaction — the row is visible to subsequent queries in
        //    this same DbContext, but not to other requests until the
        //    transaction commits at handler exit.
        // ------------------------------------------------------------------
        await customerGroupRepository.AddAsync(group, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "CreateCustomerGroup: group {GroupId} ('{Name}', salary {Amount} {Currency}) created by user {UserId}. Beginning bulk-default purchase-limit application to all existing products.",
            group.Id, group.Name, group.Salary.Amount, group.Salary.Currency, currentUser.UserId);

        // ------------------------------------------------------------------
        // 5. BULK-APPLY DEFAULT PURCHASE LIMIT (Step 5 wiring).
        //
        //    For every existing product, set a default purchase-limit row
        //    for the new group with limit = CustomerGroupPurchaseLimit.DefaultLimit
        //    (currently 1). This ensures customers in the new group
        //    immediately have a sensible per-product cap; admins can
        //    override individual limits later via the Manage Products UI.
        //
        //    BATCHING:
        //      - GetAllProductIdsAsync: single round-trip, lightweight
        //        (just IDs, no entity materialization).
        //      - Loop in batches of 200 (BulkBatchSize): load tracked
        //        products via GetByIdsAsync, call SetPurchaseLimit on each,
        //        SaveChanges (committing the batch), ClearChangeTracker
        //        (freeing memory before the next batch).
        //
        //    IDEMPOTENCY:
        //      Product.SetPurchaseLimit REPLACES any existing entry for
        //      the same group — so re-running this method on an existing
        //      group is a no-op on the data, just a wasted round-trip.
        //      The handler is only called from CreateCustomerGroup, so
        //      re-runs shouldn't happen in practice.
        //
        //    TRANSACTION SAFETY:
        //      All batches run in the ambient Wolverine transaction. If
        //      any batch fails (e.g. DB connection drops), the entire
        //      transaction rolls back — no orphan limit rows, no orphan
        //      group row. The group becomes visible to other requests
        //      only when the ambient transaction commits at handler exit.
        // ------------------------------------------------------------------
        var allProductIds = await productRepository.GetAllProductIdsAsync(cancellationToken);

        var productsUpdated = 0;
        for (int i = 0; i < allProductIds.Count; i += BulkBatchSize)
        {
            var batchIds = allProductIds.Skip(i).Take(BulkBatchSize).ToList();

            // GetByIdsAsync returns TRACKED entities (no AsNoTracking) —
            // we need tracking so EF Core detects the SetPurchaseLimit
            // mutations and generates INSERTs on SaveChanges.
            var batchProducts = await productRepository.GetByIdsAsync(batchIds, cancellationToken);

            foreach (var product in batchProducts)
            {
                // SetPurchaseLimit is idempotent — replaces any existing
                // entry for the same group. The aggregate enforces
                // GroupId != Guid.Empty + Limit >= 1 invariants.
                product.SetPurchaseLimit(group.Id, CustomerGroupPurchaseLimit.DefaultLimit);
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);

            // Clear the change tracker between batches so we don't
            // accumulate 10K tracked entities when the catalog is large.
            // Each batch is independent — we already SaveChanges'd.
            unitOfWork.ClearChangeTracker();

            productsUpdated += batchProducts.Count;

            logger.LogInformation(
                "CreateCustomerGroup: bulk-default batch {BatchStart}-{BatchEnd} ({Count} products) updated with default limit {DefaultLimit} for group {GroupId}. Cumulative: {Cumulative}/{Total}.",
                i + 1, i + batchProducts.Count, batchProducts.Count,
                CustomerGroupPurchaseLimit.DefaultLimit, group.Id,
                productsUpdated, allProductIds.Count);
        }

        logger.LogInformation(
            "CreateCustomerGroup: bulk-default complete. {ProductsUpdated} products received default purchase limit {DefaultLimit} for group {GroupId} ('{GroupName}'). Created by user {UserId}.",
            productsUpdated, CustomerGroupPurchaseLimit.DefaultLimit,
            group.Id, group.Name, currentUser.UserId);

        return Result<Guid>.Success(group.Id);
    }
}
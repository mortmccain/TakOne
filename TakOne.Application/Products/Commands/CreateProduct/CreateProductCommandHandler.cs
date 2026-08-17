using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Products.ValueObjects;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Products.Commands.CreateProduct;

/// <summary>
/// Creates a new Product in the catalog.
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class CreateProductCommandHandler
{
    public static async Task<Result<Guid>> HandleAsync
        (
        CreateProductCommand command,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        ICustomerGroupRepository customerGroupRepository,
        IUnitOfWork unitOfWork,
        ILogger<CreateProductCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {

        // ------------------------------------------------------------------
        // 0. Defensive auth check. [RequireRoles] already rejected anonymous
        //    callers via AuthorizationMiddleware, but this handler may also be
        //    invoked from tests or a non-HTTP host — re-checking keeps the
        //    invariant honest.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("CreateProduct: unauthenticated call rejected.");

            return Result<Guid>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Name uniqueness. Product names are unique across the catalog.
        //    The handler does the friendly check; the DB has a unique index
        //    as a hard guarantee against concurrent requests racing between
        //    our check and our SaveChanges.
        // ------------------------------------------------------------------
        var nameExists = await productRepository.NameExistsAsync(command.Name, excludeId: null, cancellationToken);

        if (nameExists)
        {
            logger.LogWarning
                ("CreateProduct: product name '{Name}' already exists. User {UserId} rejected.",
                command.Name, currentUser.UserId);

            return Result<Guid>.Failure
                ($"A product with the name '{command.Name}' already exists. " + "Choose a different name.");
        }

        // ------------------------------------------------------------------
        // 2. Cross-aggregate category hierarchy validation.
        //    The Product aggregate only checks "SubSub requires Sub" (a
        //    self-contained invariant). It cannot verify that SubCategoryId
        //    belongs to CategoryId, because that requires loading the
        //    Category aggregate. We delegate to the dedicated repository
        //    methods (which the Infrastructure layer implements efficiently
        //    via SQL EXISTS queries — no need to load the whole aggregate).
        // ------------------------------------------------------------------
        var categoryExists = await categoryRepository.ExistsAsync(command.CategoryId, cancellationToken);

        if (!categoryExists)
        {
            logger.LogWarning
                ("CreateProduct: category '{CategoryId}' not found. User {UserId} rejected.", command.CategoryId, currentUser.UserId);

            return Result<Guid>.Failure($"Category '{command.CategoryId}' was not found.");
        }

        if (command.SubCategoryId.HasValue)
        {
            var subBelongsToCategory = await categoryRepository.SubCategoryBelongsToCategoryAsync
                (command.CategoryId, command.SubCategoryId.Value, cancellationToken);

            if (!subBelongsToCategory)
            {
                logger.LogWarning
                    ("CreateProduct: subcategory '{SubCategoryId}' does not belong to category '{CategoryId}'. User {UserId} rejected.",
                    command.SubCategoryId, command.CategoryId, currentUser.UserId);

                return Result<Guid>.Failure
                    ($"SubCategory '{command.SubCategoryId}' does not belong to Category '{command.CategoryId}'.");
            }

            if (command.SubSubCategoryId.HasValue)
            {
                var subSubBelongsToSub = await categoryRepository.SubSubCategoryBelongsToSubCategoryAsync
                     (command.SubCategoryId.Value, command.SubSubCategoryId.Value, cancellationToken);

                if (!subSubBelongsToSub)
                {
                    logger.LogWarning
                        ("CreateProduct: subsubcategory '{SubSubCategoryId}' does not belong tosubcategory '{SubCategoryId}'. User {UserId} rejected.",
                        command.SubSubCategoryId, command.SubCategoryId, currentUser.UserId);

                    return Result<Guid>.Failure
                        ($"SubSubCategory '{command.SubSubCategoryId}' does not belong to SubCategory '{command.SubCategoryId}'.");
                }
            }
        }

        // ------------------------------------------------------------------
        // 3. Construct the domain Money value object from the DTO.
        //    The Money constructor throws DomainException on invalid input
        //    (e.g. wrong-length currency) — caught by middleware and
        //    converted to a Result.Failure.
        // ------------------------------------------------------------------
        var price = new Money(command.Price.Amount, command.Price.Currency);

        // ------------------------------------------------------------------
        // 4. Create the Product via the aggregate's factory method.
        //    Parameter order on Product.Create is:
        //      (name, description, price, stockQuantity, categoryId,
        //       pictureUrl?, subCategoryId?, subSubCategoryId?)
        //    Using named arguments so the call site is self-documenting
        //    and survives future parameter reordering.
        // ------------------------------------------------------------------
        var product = Product.Create
            (
            name: command.Name,
            description: command.Description,
            price: price,
            stockQuantity: command.InitialStockQuantity,
            categoryId: command.CategoryId,
            pictureUrl: command.PictureUrl,
            subCategoryId: command.SubCategoryId,
            subSubCategoryId: command.SubSubCategoryId
            );

        // ------------------------------------------------------------------
        // 4b. Attach per-group purchase limits.
        //
        //     TWO-PHASE FLOW (Step 5 wiring):
        //
        //     Phase 1 — AUTO-DEFAULT: For every ACTIVE CustomerGroup in
        //         the catalog, set a default limit (=
        //         CustomerGroupPurchaseLimit.DefaultLimit, currently 1)
        //         for the new Product. This ensures that when a new
        //         product is created, every existing group has a sane
        //         per-product cap by default — admins can override
        //         individual limits later via the Manage Products UI.
        //
        //         Inactive groups are SKIPPED — they're not actively
        //         used, so creating limit rows for them is wasteful.
        //         If the admin reactivates a group later, they can run
        //         a separate bulk-default flow (or the reactivation
        //         handler can do it — not implemented in Step 5).
        //
        //     Phase 2 — USER OVERRIDES: Apply the user-specified
        //         PurchaseLimits from the command. Because
        //         SetPurchaseLimit REPLACES any existing entry for the
        //         same group, the user-specified values OVERRIDE the
        //         defaults set in Phase 1.
        //
        //     DDD INVARIANT: we DON'T pass domain value objects
        //     (CustomerGroupPurchaseLimit) in from the command — that would
        //     let the application layer construct domain VOs directly,
        //     bypassing the aggregate. Instead the command carries a flat
        //     DTO (PurchaseLimitInputDto with just GroupId + Limit), and
        //     we delegate VO creation to Product.SetPurchaseLimit, which
        //     internally calls CustomerGroupPurchaseLimit.Create + replaces
        //     any existing entry for the same group.
        //
        //     Duplicate-group deduplication is handled by SetPurchaseLimit
        //     itself (last entry wins). We still validate that the same
        //     group isn't listed twice with different limits — that's a
        //     user input error, not a domain invariant, so we surface it as
        //     a friendly Result.Failure before touching the aggregate.
        //
        //     SALARY FEATURE (Step 3): entries are identified by GroupId
        //     (Guid), not GroupName. The handler trusts the UI to have
        //     validated that the GroupId references an existing
        //     CustomerGroup row — we don't re-check existence here for
        //     each entry (would N+1 the DB). The Product aggregate only
        //     enforces that GroupId is not Guid.Empty.
        // ------------------------------------------------------------------

        // ---- Phase 2 validation (user-specified entries) — run BEFORE
        //      Phase 1 so we fail-fast on duplicate group entries in
        //      the command without having applied any defaults first.
        //      (Phase 1's defaults are also idempotent — they get
        //      overridden by Phase 2 — so applying them first is safe,
        //      but failing before any work is friendlier.)
        if (command.PurchaseLimits is { Count: > 0 })
        {
            var seenGroups = new HashSet<Guid>();
            foreach (var entry in command.PurchaseLimits)
            {
                if (!seenGroups.Add(entry.GroupId))
                {
                    logger.LogWarning
                        ("CreateProduct: duplicate purchase-limit entry for group {GroupId}. User {UserId} rejected.",
                        entry.GroupId, currentUser.UserId);

                    return Result<Guid>.Failure
                        ($"Duplicate purchase limit for the same group. Each group may only appear once.");
                }
            }
        }

        // ---- Phase 1: AUTO-DEFAULT for every ACTIVE group.
        //
        //      GetAllAsync(includeInactive: false) returns only groups
        //      with IsActive=true. The list is expected to be small
        //      (5-15 rows), so no batching needed — single round-trip,
        //      single mutation loop, single SaveChanges at the end.
        //
        //      For each active group, call SetPurchaseLimit with the
        //      default limit. SetPurchaseLimit is idempotent — replaces
        //      any existing entry for the same group. The user-specified
        //      entries in Phase 2 will then OVERRIDE these defaults.
        // ------------------------------------------------------------------
        var activeGroups = await customerGroupRepository.GetAllAsync
            (includeInactive: false, cancellationToken);

        foreach (var activeGroup in activeGroups)
        {
            product.SetPurchaseLimit(activeGroup.Id, CustomerGroupPurchaseLimit.DefaultLimit);
        }

        if (activeGroups.Count > 0)
        {
            logger.LogInformation
                ("CreateProduct: auto-applied default purchase limit {DefaultLimit} for {GroupCount} active groups to new product '{ProductName}'.",
                 CustomerGroupPurchaseLimit.DefaultLimit, activeGroups.Count, command.Name);
        }

        // ---- Phase 2: USER OVERRIDES (replace defaults for explicitly-
        //               specified groups).
        //
        //      SetPurchaseLimit REPLACES the entry for the same group,
        //      so user-specified values OVERRIDE the defaults from Phase 1.
        //      Groups NOT listed in the command keep their default limit.
        // ------------------------------------------------------------------
        if (command.PurchaseLimits is { Count: > 0 })
        {
            foreach (var entry in command.PurchaseLimits)
            {
                // SetPurchaseLimit calls CustomerGroupPurchaseLimit.Create
                // internally, which enforces GroupId != Guid.Empty +
                // Limit (>=1). Invalid values throw DomainException →
                // middleware converts to Result.Failure with a friendly
                // message.
                product.SetPurchaseLimit(entry.GroupId, entry.Limit);
            }

            logger.LogInformation
                ("CreateProduct: applied {OverrideCount} user-specified purchase-limit override(s) for new product '{ProductName}'.",
                 command.PurchaseLimits.Count, command.Name);
        }

        // ------------------------------------------------------------------
        // 5. Persist. EF Core tracks the Product and its owned collection of
        //    CustomerGroupPurchaseLimit value objects as a single unit.
        // ------------------------------------------------------------------
        await productRepository.AddAsync(product, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            (
            "CreateProduct: product {ProductId} ({Name}) created by user {UserId}. Initial stock: {Stock}.",
            product.Id, product.Name, currentUser.UserId, product.StockQuantity
            );

        return Result<Guid>.Success(product.Id);
    }
}
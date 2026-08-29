using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Products.Queries.GetProductsPaginated;

/// <summary>
/// Handler for <see cref="GetProductsPaginatedQuery"/>.
///
/// The repository's <c>GetPaginatedAsync</c> accepts the same filter
/// arguments as this query, so most of the work is just clamping
/// parameters and projecting to the DTO.
///
/// CATEGORY HIERARCHY ENRICHMENT:
///   After loading the page of products, the handler makes a SINGLE call
///   to <see cref="ICategoryRepository.GetAllAsync"/> (active AND inactive
///   categories, full hierarchy) and resolves each product's
///   CategoryId / SubCategoryId / SubSubCategoryId against that tree.
///   This is done in-memory because the Category aggregate is a separate
///   aggregate from Product — we don't want to introduce a SQL JOIN across
///   aggregate boundaries (the Product table only stores Guid FKs, no
///   navigation properties). One round-trip for the category tree is
///   cheaper than one round-trip per product for its category names.
/// </summary>
public sealed class GetProductsPaginatedQueryHandler
{
    private const int MaxPageSize = 100;

    public static async Task<PaginatedResult<ProductListItemDto>> HandleAsync
        (
        GetProductsPaginatedQuery query,
        ICurrentUserService currentUser,
        IProductRepository productRepository,
        ICategoryRepository categoryRepository,
        IUserRepository userRepository,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        ILogger<GetProductsPaginatedQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetProductsPaginated: unauthenticated call rejected.");

            return new PaginatedResult<ProductListItemDto>(Array.Empty<ProductListItemDto>(), 0, 1, 1);
        }

        // ------------------------------------------------------------------
        // 1. Clamp page parameters.
        // ------------------------------------------------------------------
        var pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
        var pageSize = query.PageSize < 1
            ? 20
            : query.PageSize > MaxPageSize
                ? MaxPageSize
                : query.PageSize;

        // ------------------------------------------------------------------
        // 2. Authorization override on IncludeInactive. Only Admin/Manager
        //    may see inactive products; everyone else silently gets active
        //    only. We don't log this as a warning — a customer's UI setting
        //    the flag is a UX bug, not an attack; just clamp silently.
        //
        //    NOTE: "Inactive" here currently means "stock is zero" because
        //    the Product aggregate does NOT have an IsActive flag (deactivation
        //    is implemented as SetStock(0) per the business rule documented
        //    on DeactivateProductCommand). When the domain gains a real
        //    IsActive flag, this filter should switch to that flag.
        // ------------------------------------------------------------------
        var includeInactive = query.IncludeInactive;

        if (includeInactive)
        {
            var canSeeInactive =
                currentUser.IsInRole(Roles.Admin) ||
                currentUser.IsInRole(Roles.Manager);

            if (!canSeeInactive)
            {
                includeInactive = false;
            }
        }

        // ------------------------------------------------------------------
        // 3. Load the category tree ONCE — active AND inactive, with full
        //    SubCategory → SubSubCategory hierarchy. We need inactive
        //    categories too because a product may reference a deactivated
        //    category (the FK is just a Guid, soft-delete doesn't break
        //    referential integrity). The UI needs to surface the
        //    "(deactivated)" hint to the admin so they can decide whether
        //    to re-categorize.
        //
        //    Single round-trip — GetAllAsync eager-loads the hierarchy.
        //
        //    LOADED BEFORE THE PRODUCT PAGE (reordered): the active-id sets
        //    computed below feed the customer-visibility filter that is
        //    pushed INTO the SQL query, so pagination is computed over
        //    exactly the rows the caller can see.
        // ------------------------------------------------------------------
        List<Domain.Categories.Entities.Category>? categories = null;
        var categoriesLoaded = false;
        try
        {
            categories = await categoryRepository.GetAllAsync(cancellationToken);
            categoriesLoaded = true;
        }
        catch (Exception ex)
        {
            // Non-fatal — if the category load fails we still return the
            // products with empty category-name fields. The UI renders an
            // em-dash for empty category names, which is acceptable. The
            // customer-visibility filter degrades to in-stock-only (null
            // id-sets) instead of hiding the whole catalog.
            logger.LogWarning(ex,
                "GetProductsPaginated: failed to load category tree for enrichment. "
                + "Products will be returned without category names.");
            categories = new List<Domain.Categories.Entities.Category>();
        }

        // Build flat lookup maps for O(1) resolution.
        // Top-level: CategoryId → (Name, IsActive)
        // Mid:       SubCategoryId → (Name, IsActive)
        // Leaf:      SubSubCategoryId → (Name, IsActive)
        var categoryById = categories.ToDictionary(c => c.Id);
        var subCategoryById = new Dictionary<Guid, (string Name, bool IsActive)>();
        var subSubCategoryById = new Dictionary<Guid, (string Name, bool IsActive)>();

        foreach (var cat in categories)
        {
            foreach (var sub in cat.SubCategories)
            {
                subCategoryById[sub.Id] = (sub.Name, sub.IsActive);
                foreach (var subSub in sub.SubSubCategories)
                {
                    subSubCategoryById[subSub.Id] = (subSub.Name, subSub.IsActive);
                }
            }
        }

        // ------------------------------------------------------------------
        // 3a. CUSTOMER-VISIBILITY FILTER — pushed INTO the SQL query.
        //
        // For non-staff callers (includeInactive=false) the catalog must
        // hide zero-stock products and products under deactivated
        // categories. Historically these predicates ran in the HANDLER
        // AFTER the repository paginated — producing partially-empty
        // pages and a TotalCount the pager couldn't trust. The id-sets
        // below (derived from the category tree above) are passed to the
        // repository so the predicates compose into the single SQL
        // statement; see ProductVisibilityFilter for the null-vs-empty
        // set semantics. When the category tree failed to load, the
        // id-sets are null and only the in-stock predicate applies
        // (graceful degradation).
        // ------------------------------------------------------------------
        ProductVisibilityFilter? visibility = null;
        if (!includeInactive)
        {
            visibility = new ProductVisibilityFilter(
                ActiveCategoryIds: categoriesLoaded
                    ? categories!.Where(c => c.IsActive).Select(c => c.Id).ToList()
                    : null,
                ActiveSubCategoryIds: categoriesLoaded
                    ? subCategoryById.Where(kv => kv.Value.IsActive).Select(kv => kv.Key).ToList()
                    : null,
                ActiveSubSubCategoryIds: categoriesLoaded
                    ? subSubCategoryById.Where(kv => kv.Value.IsActive).Select(kv => kv.Key).ToList()
                    : null);
        }

        // ------------------------------------------------------------------
        // 3b. Load the page of products — with the visibility predicates
        //     applied at the DATABASE level, so pages are full and
        //     TotalCount matches what the caller can actually see.
        // ------------------------------------------------------------------
        var paginated = await productRepository.GetPaginatedAsync
            (
            categoryId: query.CategoryId,
            subCategoryId: query.SubCategoryId,
            subSubCategoryId: query.SubSubCategoryId,
            searchTerm: query.SearchTerm,
            pageNumber: pageNumber,
            pageSize: pageSize,
            visibility: visibility,
            sortBy: query.SortBy ?? ProductSortBy.Name,
            cancellationToken: cancellationToken
            );

        // ------------------------------------------------------------------
        // 5. Project to DTO.
        //
        //    STOCK-BASED "INACTIVE" FILTER:
        //    Until the domain gains a real IsActive flag, "inactive" means
        //    StockQuantity == 0. If includeInactive=false, hide zero-stock
        //    products from non-staff callers. (Staff callers always pass
        //    includeInactive=true from the AdminProducts page.)
        //
        //    MY PURCHASE LIMIT:
        //    The current user's per-product limit is resolved here. We
        //    load the user's GroupId FRESH from the database (not from
        //    the GroupId claim on the auth cookie) because the claim is
        //    a snapshot from login time and goes stale when an admin
        //    assigns the user to a group after they're already logged in.
        //    Reading from the DB guarantees the limit reflects the user's
        //    CURRENT group, so the Add button grays out correctly even
        //    without a re-login.
        //
        //    Same fix applied to GetActiveCartForUserQueryHandler and
        //    CreateOrAppendSaleCommandHandler — all three used to read
        //    currentUser.GroupName (claim) and were inconsistent with
        //    UpdateSaleLineItemCommandHandler / AddItemToSaleCommandHandler
        //    which already loaded the customer from the DB.
        // ------------------------------------------------------------------
        var searchTerm = query.SearchTerm?.Trim();
        var hasSearch = !string.IsNullOrWhiteSpace(searchTerm);

        // Load the current user's GroupId FRESH from the DB. Single
        // round-trip; cached in `groupId` for the per-product lookup
        // below. If the user is staff (no GroupId in the DB either),
        // groupId is null and no per-product cap applies.
        var freshUser = await userRepository.GetByIdAsync(currentUser.UserId, cancellationToken);
        var groupId = freshUser?.GroupId;

        // ------------------------------------------------------------------
        // Resolve the caller's per-product count limit for EACH product on
        // this page, going through IPurchaseLimitPolicy.GetCountLimitAsync
        // so that the LimitMode (CountOnly / SalaryOnly / Both) is honored.
        // This is the Step 12-c runtime fix: previously this method called
        // product.GetPurchaseLimitForGroup(groupId)?.Limit DIRECTLY inside
        // the .Select projection, which bypassed the policy and returned
        // the configured limit even when LimitMode was SalaryOnly — the
        // Products grid's "+1" button then got disabled at the configured
        // limit (e.g. 1), preventing the customer from buying more than
        // 1 unit even though the backend wouldn't have enforced the count
        // limit in SalaryOnly mode.
        //
        // IPurchaseLimitPolicy.GetCountLimitAsync already short-circuits
        // correctly: returns null when groupId is null, when LimitMode is
        // SalaryOnly, or when the product has no limit set for this group.
        //
        // We resolve all limits up-front into a dictionary (one batched
        // round-trip — see GetCountLimitsAsync) so the .Select projection
        // below stays synchronous.
        // ------------------------------------------------------------------
        // BATCHED limit resolution (one round-trip for the whole page).
        // The previous per-product loop was an N+1: up to pageSize (≤100)
        // sequential DB round-trips on every page render.
        var limitByProductId = await purchaseLimitPolicy.GetCountLimitsAsync
            (paginated.Items.Select(p => p.Id).ToList(), groupId, cancellationToken);

        // NOTE: the in-stock and category-deactivated filters that used to
        // run HERE (after DB pagination) now run INSIDE the SQL query via
        // the ProductVisibilityFilter passed to GetPaginatedAsync above —
        // pages are full and TotalCount/pager math is exact. The
        // IsProductCategoryHierarchyActive helper below is still used by
        // other call sites for point lookups.
        var dtos = paginated.Items
            .Where(p => !hasSearch ||
                        p.Name.Contains(searchTerm!, StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                // Resolve category names + active flags. Lookups use
                // TryGetValue so a missing category (e.g. hard-deleted —
                // should not happen since Category uses soft-delete) yields
                // empty name + IsActive=false, which the UI will render as
                // a red "(deactivated)" em-dash.
                var catResolved = categoryById.TryGetValue(p.CategoryId, out var cat)
                                  && cat is not null;
                var catName = catResolved ? cat!.Name : string.Empty;
                var catActive = catResolved && cat!.IsActive;

                string subName = string.Empty;
                var subActive = true;
                if (p.SubCategoryId is not null
                    && subCategoryById.TryGetValue(p.SubCategoryId.Value, out var sub))
                {
                    subName = sub.Name;
                    subActive = sub.IsActive;
                }

                string subSubName = string.Empty;
                var subSubActive = true;
                if (p.SubSubCategoryId is not null
                    && subSubCategoryById.TryGetValue(p.SubSubCategoryId.Value, out var subSub))
                {
                    subSubName = subSub.Name;
                    subSubActive = subSub.IsActive;
                }

                return new ProductListItemDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = p.Description ?? string.Empty,
                    PictureUrl = p.PictureUrl,

                    Price = new MoneyDto
                    {
                        Amount = p.Price.Amount,
                        Currency = p.Price.Currency
                    },

                    StockQuantity = p.StockQuantity,

                    CategoryId = p.CategoryId,
                    SubCategoryId = p.SubCategoryId,
                    SubSubCategoryId = p.SubSubCategoryId,

                    CategoryName = catName,
                    CategoryIsActive = catActive,
                    SubCategoryName = subName,
                    SubCategoryIsActive = subActive,
                    SubSubCategoryName = subSubName,
                    SubSubCategoryIsActive = subSubActive,

                    MyPurchaseLimit = limitByProductId.TryGetValue(p.Id, out var lim)
                        ? lim
                        : null
                };
            })
            .ToList();

        return new PaginatedResult<ProductListItemDto>(dtos, paginated.TotalCount, pageNumber, pageSize);
    }

    /// <summary>
    /// Returns <c>true</c> only if every level of the product's category
    /// hierarchy that IS set is currently active. A null SubCategoryId /
    /// SubSubCategoryId is treated as "not set, skip". A missing-from-tree
    /// Category (e.g. hard-deleted — should not happen since Category uses
    /// soft-delete, but defensive) is treated as inactive.
    ///
    /// Used by the customer-facing <c>includeInactive == false</c> path
    /// to hide products whose category was deactivated, WITHOUT zeroing
    /// their StockQuantity.
    /// </summary>
    private static bool IsProductCategoryHierarchyActive(
        Domain.Products.Entities.Product p,
        Dictionary<Guid, Domain.Categories.Entities.Category> categoryById,
        Dictionary<Guid, (string Name, bool IsActive)> subCategoryById,
        Dictionary<Guid, (string Name, bool IsActive)> subSubCategoryById)
    {
        // Top-level Category — required on Product, always set.
        if (!categoryById.TryGetValue(p.CategoryId, out var cat) || cat is null || !cat.IsActive)
        {
            return false;
        }

        // SubCategory — optional. If set, must be active.
        if (p.SubCategoryId is not null)
        {
            if (!subCategoryById.TryGetValue(p.SubCategoryId.Value, out var sub) || !sub.IsActive)
            {
                return false;
            }
        }

        // SubSubCategory — optional. If set, must be active.
        if (p.SubSubCategoryId is not null)
        {
            if (!subSubCategoryById.TryGetValue(p.SubSubCategoryId.Value, out var subSub) || !subSub.IsActive)
            {
                return false;
            }
        }

        return true;
    }
}
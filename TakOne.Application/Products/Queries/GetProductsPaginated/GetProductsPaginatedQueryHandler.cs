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
/// The repository's <c>GetPaginatedAsync</c> accepts a
/// <see cref="ProductsListFilters"/> aggregate (Round 6 — same shape as the
/// users list's <c>UsersListFilters</c>), so most of the work is clamping
/// parameters, resolving the category-NAME filters against the category
/// tree, and projecting to the DTO.
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
///
/// CATEGORY-NAME FILTER RESOLUTION (Round 6):
///   The same tree load doubles as the resolver for the grid's category
///   NAME filters: the AdminProducts grid filters its Category columns by
///   free text, but Product rows store only Guid FKs — so the handler
///   resolves each name filter to the set of category Ids whose display
///   name matches (all six text operators, case-insensitive) and hands the
///   Id sets to the repository, which applies them as SQL IN clauses. This
///   reuses the exact id-set technique <see cref="ProductVisibilityFilter"/>
///   pioneered for the customer-visibility predicates. NULL vs EMPTY set
///   semantics (null = no filter; empty = nothing matches) mirror that
///   filter's contract; when the tree load itself fails, the name filters
///   degrade to null (no clause) rather than zeroing the grid — the same
///   graceful-degradation posture the visibility filter takes.
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
        //    exactly the rows the caller can see. (Round 6: the same tree
        //    also resolves the category-NAME filters into Id sets — see
        //    the class doc.)
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
            // id-sets) instead of hiding the whole catalog, and the
            // category-NAME filters degrade to no-clause (null id-sets)
            // instead of zeroing the grid — both documented degradations.
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
        // 3b. Resolve the category-NAME filters (Round 6) into Id sets.
        //
        // Each of the grid's three category columns filters by free text
        // on the RESOLVED display name; the product row stores only the
        // Guid FK, so the name match happens HERE — over the tree we
        // already loaded — and the repository applies the resulting Id
        // sets as IN clauses. NULL = no filter (including the
        // tree-load-failed degradation); EMPTY = "no category matches
        // the term", which correctly yields zero rows.
        // ------------------------------------------------------------------
        var categoryIds = ResolveCategoryIds(
            categories.Select(c => (c.Id, c.Name)),
            query.CategoryNameFilter,
            categoriesLoaded);
        var subCategoryIds = ResolveCategoryIds(
            subCategoryById.Select(kv => (kv.Key, kv.Value.Name)),
            query.SubCategoryNameFilter,
            categoriesLoaded);
        var subSubCategoryIds = ResolveCategoryIds(
            subSubCategoryById.Select(kv => (kv.Key, kv.Value.Name)),
            query.SubSubCategoryNameFilter,
            categoriesLoaded);

        // ------------------------------------------------------------------
        // 3c. Load the page of products — with ALL filters (legacy +
        //     Round-6 column filters) and the visibility predicates
        //     applied at the DATABASE level, so pages are full and
        //     TotalCount matches what the caller can actually see.
        //
        //     The pre-Round-6 AdminProducts page loaded PageSize=100 once
        //     and filtered/sorted CLIENT-side — every product past the
        //     first 100 (name-ordered) was invisible to staff. The grid
        //     now runs Radzen LoadData mode (Round 5's proven pattern)
        //     and this handler is its single source of truth.
        // ------------------------------------------------------------------
        var filters = new ProductsListFilters(
            SearchTerm: query.SearchTerm,
            CategoryId: query.CategoryId,
            SubCategoryId: query.SubCategoryId,
            SubSubCategoryId: query.SubSubCategoryId,
            Name: query.NameFilter,
            StockStatus: query.StockStatus,
            Price: query.PriceFilter,
            Stock: query.StockFilter,
            CategoryIds: categoryIds,
            SubCategoryIds: subCategoryIds,
            SubSubCategoryIds: subSubCategoryIds,
            // Null SortBy = the explicit Name default (kept from Round 4 so
            // the repository's contract is exercised with a defined key);
            // SortDescending is only meaningful when the caller actually
            // picked a sort — a stray direction flag must never flip the
            // default order.
            SortBy: query.SortBy ?? ProductSortBy.Name,
            SortDescending: query.SortBy is not null && query.SortDescending);

        var paginated = await productRepository.GetPaginatedAsync
            (
            filters,
            visibility,
            pageNumber,
            pageSize,
            cancellationToken
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
        //    NOTE: the search-term Name filter now runs INSIDE the SQL
        //    query (via ProductsListFilters.SearchTerm), so the historical
        //    post-pagination re-filter below is a no-op belt-and-braces
        //    pass — kept because it predates Round 6 and costs nothing on
        //    an already-filtered page.
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

    // ── Category-name → Id-set resolution (Round 6) ────────────────────

    /// <summary>
    /// Resolves one category-name text filter against the flattened
    /// (Id, Name) pairs of a category level, producing the Id set the
    /// repository applies as a SQL IN clause.
    /// <list type="bullet">
    /// <item>Null filter (or whitespace term) → <c>null</c> — no clause.</item>
    /// <item>Tree load failed (<paramref name="categoriesLoaded"/> false)
    /// → <c>null</c> — graceful degradation, same posture as the
    /// visibility filter's null id-sets (the grid stays usable rather
    /// than mysteriously empty).</item>
    /// <item>No category matches → an EMPTY set — the term legitimately
    /// matches nothing, so the filtered result is zero rows (the page
    /// shows its filtered-empty state with a Clear-all escape hatch).</item>
    /// </list>
    /// The name comparison is case-insensitive in memory
    /// (<see cref="StringComparison.OrdinalIgnoreCase"/>), matching what
    /// the SQL-side name filter achieves via LOWER() and what the grid's
    /// FilterCaseSensitivity=CaseInsensitive promised client-side.
    /// </summary>
    private static IReadOnlyCollection<Guid>? ResolveCategoryIds(
        IEnumerable<(Guid Id, string Name)> level,
        ProductsTextFilter? filter,
        bool categoriesLoaded)
    {
        var term = filter?.Value?.Trim();
        if (filter is null || string.IsNullOrEmpty(term) || !categoriesLoaded)
        {
            return null;
        }

        return level
            .Where(entry => MatchesCategoryName(entry.Name, term, filter.Operator))
            .Select(entry => entry.Id)
            .ToList();
    }

    /// <summary>
    /// Applies one <see cref="ProductsTextOperator"/> to a category
    /// display name, case-insensitively. In-memory by design: the name
    /// lives on the Category aggregate, not on the Product row, so the
    /// match happens here (over the already-loaded tree) and only the
    /// resulting Id set travels into SQL.
    /// </summary>
    private static bool MatchesCategoryName(string name, string term, ProductsTextOperator op)
    {
        return op switch
        {
            ProductsTextOperator.Contains => name.Contains(term, StringComparison.OrdinalIgnoreCase),
            ProductsTextOperator.NotContains => !name.Contains(term, StringComparison.OrdinalIgnoreCase),
            ProductsTextOperator.Equals => string.Equals(name, term, StringComparison.OrdinalIgnoreCase),
            ProductsTextOperator.NotEquals => !string.Equals(name, term, StringComparison.OrdinalIgnoreCase),
            ProductsTextOperator.StartsWith => name.StartsWith(term, StringComparison.OrdinalIgnoreCase),
            ProductsTextOperator.EndsWith => name.EndsWith(term, StringComparison.OrdinalIgnoreCase),
            // Unknown operator values (a malformed message could carry an
            // out-of-range enum) match nothing here — but the empty result
            // set is then indistinguishable from "no category matches",
            // which is the same lenient dead-end the SQL filters choose.
            _ => false
        };
    }
}

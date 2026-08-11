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
        // 3. Load the page of products.
        // ------------------------------------------------------------------
        var paginated = await productRepository.GetPaginatedAsync
            (
            categoryId: query.CategoryId,
            subCategoryId: query.SubCategoryId,
            subSubCategoryId: query.SubSubCategoryId,
            searchTerm: query.SearchTerm,
            pageNumber: pageNumber,
            pageSize: pageSize,
            cancellationToken: cancellationToken
            );

        // ------------------------------------------------------------------
        // 4. Load the category tree ONCE — active AND inactive, with full
        //    SubCategory → SubSubCategory hierarchy. We need inactive
        //    categories too because a product may reference a deactivated
        //    category (the FK is just a Guid, soft-delete doesn't break
        //    referential integrity). The UI needs to surface the
        //    "(deactivated)" hint to the admin so they can decide whether
        //    to re-categorize.
        //
        //    Single round-trip — GetAllAsync eager-loads the hierarchy.
        // ------------------------------------------------------------------
        List<Domain.Categories.Entities.Category>? categories = null;
        try
        {
            categories = await categoryRepository.GetAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Non-fatal — if the category load fails we still return the
            // products with empty category-name fields. The UI renders an
            // em-dash for empty category names, which is acceptable.
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
        //    load the user's GroupName FRESH from the database (not from
        //    the GroupName claim on the auth cookie) because the claim is
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

        // Load the current user's GroupName FRESH from the DB. Single
        // round-trip; cached in `groupName` for the per-product lookup
        // below. If the user is staff (no GroupName in the DB either),
        // groupName is null and no per-product cap applies.
        var freshUser = await userRepository.GetByIdAsync(currentUser.UserId, cancellationToken);
        var groupName = freshUser?.GroupName;

        var dtos = paginated.Items
            .Where(p => includeInactive || p.StockQuantity > 0)
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

                    MyPurchaseLimit = !string.IsNullOrWhiteSpace(groupName)
                        ? p.GetPurchaseLimitForGroup(groupName)?.Limit
                        : null
                };
            })
            .ToList();

        return new PaginatedResult<ProductListItemDto>(dtos, paginated.TotalCount, pageNumber, pageSize);
    }
}

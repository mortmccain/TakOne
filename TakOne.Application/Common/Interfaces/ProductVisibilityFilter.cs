namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Customer-facing visibility predicates for the product catalog query.
///
/// WHY THIS EXISTS:
///   The customer-facing Products page must hide (a) zero-stock products
///   and (b) products whose Category / SubCategory / SubSubCategory has
///   been deactivated. Historically these two predicates were applied in
///   the HANDLER, AFTER the repository had already paginated at the
///   database level — so DB pages came back partially empty (e.g. 12 of
///   20 slots), TotalCount included rows the customer can never see, and
///   the pager math was wrong. Pushing the predicates INTO the SQL query
///   makes pagination exact.
///
/// NULL SETS = "CATEGORY STATE UNKNOWN":
///   A null id-set means "do not filter by this level" — used when the
///   category-tree load failed (the handler degrades gracefully and still
///   returns in-stock products rather than an empty catalog). An EMPTY
///   (non-null) set means "every category at this level is deactivated"
///   and filters everything that references the level.
/// </summary>
/// <param name="ActiveCategoryIds">
/// Ids of ACTIVE top-level categories. Products whose
/// <c>CategoryId</c> is not in this set are excluded. Null = skip this
/// predicate.
/// </param>
/// <param name="ActiveSubCategoryIds">
/// Ids of ACTIVE sub-categories. Products with a non-null
/// <c>SubCategoryId</c> not in this set are excluded (products with no
/// sub-category pass). Null = skip this predicate.
/// </param>
/// <param name="ActiveSubSubCategoryIds">
/// Ids of ACTIVE sub-sub-categories. Products with a non-null
/// <c>SubSubCategoryId</c> not in this set are excluded (products with no
/// sub-sub-category pass). Null = skip this predicate.
/// </param>
public sealed record ProductVisibilityFilter(
    IReadOnlyCollection<Guid>? ActiveCategoryIds = null,
    IReadOnlyCollection<Guid>? ActiveSubCategoryIds = null,
    IReadOnlyCollection<Guid>? ActiveSubSubCategoryIds = null);

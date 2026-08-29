namespace TakOne.Application.Products.Queries.GetProductsPaginated;

/// <summary>
/// Text-match operators for the products list's server-side string column
/// filters (Round 6 — server-driven paging for the AdminProducts grid).
/// Mirrors the Radzen <c>FilterOperator</c> values the grid's filter row can
/// emit for string columns; the WebUI layer translates Radzen's operator enum
/// to this one so the Application layer stays free of Radzen dependencies
/// (same split as the users list's <see cref="UsersTextOperator"/> and the
/// sales list's <c>SalesTextOperator</c>).
/// </summary>
public enum ProductsTextOperator
{
    /// <summary>Substring match (case-insensitive).</summary>
    Contains = 1,

    /// <summary>Excludes rows where the value contains the term.</summary>
    NotContains = 2,

    /// <summary>Exact match (case-insensitive).</summary>
    Equals = 3,

    /// <summary>Excludes rows with the exact value.</summary>
    NotEquals = 4,

    /// <summary>Prefix match (case-insensitive).</summary>
    StartsWith = 5,

    /// <summary>Suffix match (case-insensitive).</summary>
    EndsWith = 6
}

/// <summary>
/// Numeric-comparison operators for the products list's server-side Price
/// and Stock column filters (Round 6). Mirrors the Radzen
/// <c>FilterOperator</c> values a numeric column's filter menu can emit
/// (same set as the sales list's <c>SalesAmountOperator</c>).
/// </summary>
public enum ProductsNumberOperator
{
    /// <summary>Column value equals the operand.</summary>
    Equals = 1,

    /// <summary>Column value differs from the operand.</summary>
    NotEquals = 2,

    /// <summary>Column value strictly greater than the operand.</summary>
    GreaterThan = 3,

    /// <summary>Column value greater than or equal to the operand.</summary>
    GreaterThanOrEqual = 4,

    /// <summary>Column value strictly less than the operand.</summary>
    LessThan = 5,

    /// <summary>Column value less than or equal to the operand.</summary>
    LessThanOrEqual = 6
}

/// <summary>
/// Stock-status filter for the products list's Status column (Round 6).
/// The column's dropdown offers exactly these two states — a product is
/// "in stock" when <c>StockQuantity &gt; 0</c> and "out of stock" when it is
/// 0 (stock is never negative; zero-stock IS the domain's deactivation, see
/// <c>DeactivateProductCommand</c>). A null filter means "All" — both
/// states — so the two-value enum plus nullability covers the dropdown's
/// three options without a meaningless <c>All</c> member travelling over
/// the wire.
/// </summary>
public enum ProductStockStatus
{
    /// <summary>Only products with <c>StockQuantity &gt; 0</c>.</summary>
    InStock = 1,

    /// <summary>Only products with <c>StockQuantity == 0</c>.</summary>
    OutOfStock = 2
}

/// <summary>
/// A single server-side text column filter (term + operator) for the
/// products list. Mirrors <see cref="UsersTextFilter"/> /
/// <c>SalesTextFilter</c>.
/// </summary>
/// <param name="Value">The raw term as typed. Trimmed by the repository;
/// an empty/whitespace term means "no filter" (lenient contract, same as
/// the users/sales lists).</param>
/// <param name="Operator">How <paramref name="Value"/> is matched.</param>
public sealed record ProductsTextFilter(string Value, ProductsTextOperator Operator);

/// <summary>
/// A single server-side numeric column filter (operator + operand) for the
/// products list — used by both the Price (<c>Price.Amount</c>, decimal) and
/// Stock (<c>StockQuantity</c>, int) columns. The operand is a decimal so a
/// single record serves both; the repository converts the operand to the
/// column's CLR type when the target is the int stock column (see the
/// repository's STOCK OPERAND CONVERSION note — a type-mismatched
/// comparison silently breaks on SQLite and degrades indexes on SQL
/// Server).
/// </summary>
/// <param name="Operator">The comparison to apply.</param>
/// <param name="Value">The comparison operand — a raw amount in the
/// product's own currency for the Price filter (the filter is
/// currency-blind by design: the grid column filters on the underlying
/// decimal), or a raw unit count for the Stock filter.</param>
public sealed record ProductsNumberFilter(ProductsNumberOperator Operator, decimal Value);

/// <summary>
/// The complete set of server-side list filters + sort applied by
/// <c>ProductRepository.GetPaginatedAsync</c> (Round 6 — server-driven
/// paging for the AdminProducts grid). Packing them into ONE aggregate keeps
/// the repository signature stable as filters evolve, and the
/// positional-record shape is serializable over Wolverine (the same shape as
/// <see cref="UsersListFilters"/> and <c>SalesListFilters</c>).
/// NULL = NO FILTER: every member is optional; null members add no WHERE
/// clause.
/// </summary>
/// <param name="SearchTerm">
/// Legacy cross-column search (<c>Name</c> contains, case-insensitive) —
/// used by the shop pages, the mobile catalog, and the mobile search
/// typeahead. Kept as a dedicated member (rather than folded into
/// <paramref name="Name"/>) so those callers are unchanged.
/// </param>
/// <param name="CategoryId">Legacy exact-match category filters (the shop's
/// category navigation). A CategoryId filter implies the sub-levels via the
/// repository's hierarchy semantics.</param>
/// <param name="SubCategoryId">Legacy exact-match sub-category filter.</param>
/// <param name="SubSubCategoryId">Legacy exact-match sub-sub-category
/// filter.</param>
/// <param name="Name">Per-column text filter on the product name (the
/// AdminProducts grid's Name column).</param>
/// <param name="StockStatus">Status-dropdown filter (In/Out of stock).
/// Note this composes WITH <paramref name="Stock"/>: the grid has both a
/// Stock numeric column filter and a Status dropdown, both of which
/// reference <c>StockQuantity</c> and are AND-ed in SQL.</param>
/// <param name="Price">Numeric filter on <c>Price.Amount</c>.</param>
/// <param name="Stock">Numeric filter on <c>StockQuantity</c>.</param>
/// <param name="CategoryIds">
/// CATEGORY-NAME FILTER, RESOLVED TO IDS: the AdminProducts grid filters its
/// Category column by free text, but the product row stores only the
/// category's Guid — so the QUERY HANDLER (which already loads the category
/// tree for DTO enrichment) resolves the name filter against the tree and
/// hands the repository the matched Id set. Null = no name filter active.
/// An EMPTY (non-null) set means "no category matches the term" and filters
/// out every product (same null-vs-empty semantics as
/// <see cref="ProductVisibilityFilter"/>'s id-sets, but inverted source:
/// empty here comes from a failed NAME match, not a deactivated level).
/// </param>
/// <param name="SubCategoryIds">Same resolution for the Sub-Category
/// column's name filter. Products with a null <c>SubCategoryId</c> never
/// match (their cell renders the "—" placeholder).</param>
/// <param name="SubSubCategoryIds">Same resolution for the Sub-Sub-Category
/// column's name filter.</param>
/// <param name="SortBy">Sort key; null = the Name-ascending default (the
/// pre-Round-4 order the shop relies on).</param>
/// <param name="SortDescending">Sort direction for
/// <paramref name="SortBy"/>. Applies to the user-selected key only, never
/// the tiebreaker. Only meaningful for the <c>Name</c> key in practice —
/// the price/stock keys are direction-encoded by design (see
/// <see cref="ProductSortBy"/>) — but the repository handles every
/// (key, direction) pair defensively.</param>
public sealed record ProductsListFilters(
    string? SearchTerm,
    Guid? CategoryId,
    Guid? SubCategoryId,
    Guid? SubSubCategoryId,
    ProductsTextFilter? Name,
    ProductStockStatus? StockStatus,
    ProductsNumberFilter? Price,
    ProductsNumberFilter? Stock,
    IReadOnlyCollection<Guid>? CategoryIds,
    IReadOnlyCollection<Guid>? SubCategoryIds,
    IReadOnlyCollection<Guid>? SubSubCategoryIds,
    ProductSortBy? SortBy,
    bool SortDescending);

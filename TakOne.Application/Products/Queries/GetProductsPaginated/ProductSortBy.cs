namespace TakOne.Application.Products.Queries.GetProductsPaginated;

/// <summary>
/// Sort keys for the product catalog. Born in Round 4 (shop sorting: name +
/// price) and extended in Round 6 with the stock orders the AdminProducts
/// grid needs for server-driven paging.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THE PRICE/STOCK KEYS ARE DIRECTION-ENCODED</b>: the Round-4 shop
/// sort control offers fixed orders ("cheapest first", "most expensive
/// first"), so each key names a complete order rather than a column. The
/// Round-6 grid translation keeps that vocabulary (a descending price click
/// maps to <see cref="PriceHighToLow"/>), and the companion
/// <c>SortDescending</c> flag exists for the one key that genuinely needs a
/// runtime direction — <see cref="Name"/> (alphabetical vs reverse). The
/// repository's sort switch handles every (key, direction) pair defensively
/// so a malformed combination degrades to a defined order instead of
/// throwing.
/// </para>
/// <para>
/// <b>CATEGORY SORTS ARE DELIBERATELY ABSENT (deferred)</b>: the
/// AdminProducts grid's Category / Sub-Category / Sub-Sub-Category columns
/// are filterable server-side (name → Id-set resolution, see
/// <see cref="ProductsListFilters.CategoryIds"/>) but NOT sortable — the
/// names live on the <c>Category</c> aggregate and <see cref="Domain.Products.Entities.Product"/>
/// stores only Guid FKs with no navigation property, so a name sort would
/// need a cross-aggregate LEFT JOIN in the ORDER BY (the same trade-off the
/// Round-5 users list documented for its GroupName column). The columns'
/// header sorts are disabled in the UI and a stray sort descriptor degrades
/// leniently in the translator. Revisit if users ask for it.
/// </para>
/// <para>
/// <b>WHY NO "NEWEST"</b>: <see cref="Domain.Products.Entities.Product"/>
/// has no persisted creation timestamp (no CreatedAtUtc column, and the Guid
/// Id is random, not insertion-ordered), so a newest-first order would
/// require a migration + backfill. Deferred; revisit if/when products gain
/// an audit timestamp.
/// </para>
/// <para>
/// <b>DETERMINISM</b>: every order appends a tiebreaker so OFFSET/FETCH
/// paging never skips or duplicates rows — the product NAME for the
/// price/stock orders (names are unique — <c>NameExistsAsync</c> enforces
/// it — so the tiebreaker is total), and the Id for the name orders (where
/// the name itself is the primary key of the sort and cannot break its own
/// ties).
/// </para>
/// </remarks>
public enum ProductSortBy
{
    /// <summary>Alphabetical by name (the pre-Round-4 default; Round 6 adds
    /// the descending variant via the query's SortDescending flag).</summary>
    Name = 1,

    /// <summary>Cheapest first; name breaks ties.</summary>
    PriceLowToHigh = 2,

    /// <summary>Most expensive first; name breaks ties.</summary>
    PriceHighToLow = 3,

    /// <summary>Smallest stock first; name breaks ties (Round 6 — the
    /// AdminProducts grid's Stock column).</summary>
    StockLowToHigh = 4,

    /// <summary>Largest stock first; name breaks ties (Round 6 — the
    /// AdminProducts grid's Stock column).</summary>
    StockHighToLow = 5
}

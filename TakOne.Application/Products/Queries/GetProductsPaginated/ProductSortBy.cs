namespace TakOne.Application.Products.Queries.GetProductsPaginated;

/// <summary>
/// Sort keys for the customer-facing product catalog (Round 4 — shop
/// sorting).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY ONLY THREE KEYS</b>: the shop's realistic browse orders are
/// name-alphabetical (the pre-Round-4 hard-coded order — preserved as
/// the default so nothing shifts for users who never touch the sort
/// control) and price ascending/descending (the classic
/// comparison-shopping orders). A "newest" order would need a
/// persisted creation timestamp — <see cref="Domain.Products.Entities.Product"/>
/// has none today (no CreatedAtUtc column, and the Guid Id is random,
/// not insertion-ordered), so it would require a migration + backfill.
/// Deferred; revisit if/when products gain an audit timestamp.
/// </para>
/// <para>
/// <b>DETERMINISM</b>: every non-default order appends the product name
/// as a tiebreaker so OFFSET/FETCH paging never skips or duplicates
/// rows when products share a price.
/// </para>
/// </remarks>
public enum ProductSortBy
{
    /// <summary>Alphabetical by name (the pre-Round-4 default).</summary>
    Name = 1,

    /// <summary>Cheapest first; name breaks ties.</summary>
    PriceLowToHigh = 2,

    /// <summary>Most expensive first; name breaks ties.</summary>
    PriceHighToLow = 3
}

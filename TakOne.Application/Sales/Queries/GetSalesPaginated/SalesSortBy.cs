namespace TakOne.Application.Sales.Queries.GetSalesPaginated;

/// <summary>
/// Sort keys for the sales list (Round 4 — server-driven paging).
///
/// The desktop Sales grid runs in Radzen LoadData mode: every sort click
/// re-queries the server, so the sort key must travel WITH the query and
/// be applied inside SQL (via the specification's ORDER BY) — not
/// client-side on the loaded page.
///
/// VALUES map 1:1 to the sortable columns of the sales grid:
///   <see cref="SaleNumber"/>   → ORDER BY SaleNumber.Year, SaleNumber.Sequence
///                                (the display string "INT-۱۴۰۵-۰۰۴۲" is
///                                computed-on-access and NOT mapped, so the
///                                mapped integer parts are the sortable truth)
///   <see cref="Status"/>       → ORDER BY Status (enum → int ordinal)
///   <see cref="Total"/>        → ORDER BY Total.Amount
///   <see cref="CustomerName"/> → ORDER BY CustomerName
///   <see cref="CreatedByName"/>→ ORDER BY CreatedByName
///   <see cref="CreatedAtUtc"/> → ORDER BY CreatedAtUtc (the DEFAULT —
///                                newest first — preserved from the
///                                pre-Round-4 specs)
///
/// A null <c>SortBy</c> on the query means "no user sort active" — the
/// specification falls back to <see cref="CreatedAtUtc"/> descending so
/// OFFSET/FETCH paging stays deterministic (SQL makes no row-order
/// guarantees without ORDER BY).
///
/// DRAFT ROWS under <see cref="SaleNumber"/> sorting: drafts have a NULL
/// SaleNumber, so they sort as a block (NULLs group first ascending /
/// last descending on both SQL Server and SQLite) with the Id tiebreaker
/// keeping them deterministic within the block.
/// </summary>
public enum SalesSortBy
{
    /// <summary>Sort by the sale number parts (Year, then Sequence).</summary>
    SaleNumber = 1,

    /// <summary>Sort by the sale status (enum ordinal).</summary>
    Status = 2,

    /// <summary>Sort by the sale total amount.</summary>
    Total = 3,

    /// <summary>Sort by the customer display name.</summary>
    CustomerName = 4,

    /// <summary>Sort by the creator display name.</summary>
    CreatedByName = 5,

    /// <summary>Sort by the creation timestamp (default when unset).</summary>
    CreatedAtUtc = 6
}

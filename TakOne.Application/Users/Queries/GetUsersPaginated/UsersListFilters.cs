using TakOne.Domain.Users;

namespace TakOne.Application.Users.Queries.GetUsersPaginated;

/// <summary>
/// Text-match operators for the users list's server-side string column
/// filters (Round 5 — server-driven paging). Mirrors the Radzen
/// FilterOperator values the grid's filter row can emit for string columns;
/// the WebUI layer translates Radzen's operator enum to this one so the
/// Application layer stays free of Radzen dependencies (same split as the
/// sales list's <c>SalesTextOperator</c>).
/// </summary>
public enum UsersTextOperator
{
    Contains = 1,
    NotContains = 2,
    Equals = 3,
    NotEquals = 4,
    StartsWith = 5,
    EndsWith = 6
}

/// <summary>
/// Sort keys for the users list (Round 5 — server-driven paging). The sort
/// key travels WITH the query and is applied inside SQL, not client-side on
/// the loaded page. VALUES map 1:1 to the sortable columns of the users
/// grid:
///   <list type="bullet">
///     <item><c>WorkerId</c> → ORDER BY WorkerId</item>
///     <item><c>FullName</c> → ORDER BY FullName</item>
///     <item><c>Gender</c> → ORDER BY Gender (enum → int ordinal)</item>
///     <item><c>IsActive</c> → ORDER BY IsActive</item>
///   </list>
/// The GroupName column is deliberately NOT sortable server-side: the
/// group name lives on the CustomerGroup aggregate (no navigation property
/// on User), so a name sort would require a cross-table LEFT JOIN in the
/// ORDER BY. The column stays filterable (via GroupId) — only its header
/// sort is disabled in the UI.
/// A null <see cref="UsersListFilters.SortBy"/> means "no user sort
/// active" — the repository falls back to FullName ascending (the
/// pre-Round-5 default order, which the mobile admin-users list and the
/// notification typeahead both rely on), and every sort arm carries the
/// Id tiebreaker so OFFSET/FETCH paging stays deterministic.
/// </summary>
public enum UsersSortBy
{
    WorkerId = 1,
    FullName = 2,
    Gender = 3,
    IsActive = 4
}

/// <summary>
/// A single server-side text column filter (term + operator) for the
/// users list. Mirrors <c>SalesTextFilter</c>.
/// </summary>
public sealed record UsersTextFilter(string Value, UsersTextOperator Operator);

/// <summary>
/// The complete set of server-side list filters + sort applied by
/// <c>UserRepository.GetPaginatedAsync</c> (Round 5 — server-driven
/// paging). Packing them into ONE aggregate keeps the repository signature
/// stable as filters evolve, and the positional-record shape is
/// serializable over Wolverine (the same shape as
/// <c>SalesListFilters</c>).
/// NULL = NO FILTER: every member is optional; null members add no WHERE
/// clause.
/// </summary>
/// <param name="SearchTerm">
/// Legacy cross-column OR search (WorkerId OR FullName, case-insensitive
/// contains) — used by the mobile admin-users search box and the
/// notification recipient typeahead.
/// </param>
/// <param name="GroupId">Exact group membership (FK).</param>
/// <param name="IsActive">Tristate activity filter.</param>
/// <param name="Gender">Exact gender filter (enum → int in SQL).</param>
/// <param name="WorkerId">Per-column text filter on WorkerId.</param>
/// <param name="FullName">Per-column text filter on FullName.</param>
/// <param name="SortBy">Sort key; null = the FullName-asc default.</param>
/// <param name="SortDescending">Sort direction (applies to the
/// user-selected key only, never the Id tiebreaker).</param>
public sealed record UsersListFilters(
    string? SearchTerm,
    Guid? GroupId,
    bool? IsActive,
    Gender? Gender,
    UsersTextFilter? WorkerId,
    UsersTextFilter? FullName,
    UsersSortBy? SortBy,
    bool SortDescending);

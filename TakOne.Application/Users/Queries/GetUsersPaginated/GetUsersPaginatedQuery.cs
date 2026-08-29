using TakOne.Application.Common.Authorization;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Queries.GetUsersPaginated;

/// <summary>
/// Paginated list query for Users. Returns
/// <see cref="PaginatedResult{UserListItemDto}"/>.
///
/// AUTHORIZATION MODEL:
///   - Admin / Manager: may list all users, optionally filtered by group.
///   - Employee: may list users (for the sales-employee "create sale on
///     behalf of customer" flow), but GroupName is stripped from each DTO.
///   - Customer / ReadOnly: NOT allowed to call this query. The handler
///     returns an empty page (the auth middleware should already have
///     rejected the call).
///
///   The handler enforces all three rules. The auth middleware does the
///   first cut (role check); the handler enforces the GroupName visibility
///   rule, which is per-row, not per-call.
///
/// FILTERS:
///   - <see cref="SearchTerm"/>: matched case-insensitively against WorkerId
///     and FullName.
///   - <see cref="GroupId"/>: exact match (FK to CustomerGroups.Id). Null
///     = include users from all groups (and staff users with no group).
///   - <see cref="IsActive"/>: tristate. Null = both, true = active only,
///     false = inactive only.
///
/// ROUND 5 (server-driven paging for the AdminUsers grid):
///   - <see cref="Gender"/>: exact enum match (translated to the column's
///     int storage in SQL).
///   - <see cref="WorkerIdFilter"/> / <see cref="FullNameFilter"/>: typed
///     per-column text filters (operator + value), replacing the grid's
///     client-side text filtering.
///   - <see cref="SortBy"/> + <see cref="SortDescending"/>: user-selected
///     server-side ordering with an Id tiebreaker (deterministic paging).
///     Null SortBy = the FullName-ascending default.
///   All new members are optional and null-defaulting, so the query stays
///   source-compatible with its other callers (the notification recipient
///   typeahead on desktop + mobile, and the mobile admin-users list).
///
/// SALARY FEATURE (Step 3):
///   Replaced <c>GroupName</c> (string) filter with <c>GroupId</c> (Guid).
/// </summary>
[RequireRoles(Roles.Admin, Roles.Manager, Roles.Employee)]
public sealed class GetUsersPaginatedQuery
{
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    public string? SearchTerm { get; init; }

    /// <summary>
    /// Optional filter by customer group Id. Null = all groups + staff
    /// users with no group.
    /// </summary>
    public Guid? GroupId { get; init; }

    /// <summary>
    /// Tristate activity filter. Null = both, true = active only,
    /// false = inactive only.
    /// </summary>
    public bool? IsActive { get; init; }

    /// <summary>
    /// Optional gender filter (Round 5). Null = both genders.
    /// </summary>
    public Gender? Gender { get; init; }

    /// <summary>
    /// Typed text filter for the WorkerId column (Round 5) — operator +
    /// value, evaluated in SQL. Null = no filter.
    /// </summary>
    public UsersTextFilter? WorkerIdFilter { get; init; }

    /// <summary>
    /// Typed text filter for the FullName column (Round 5) — operator +
    /// value, evaluated in SQL. Null = no filter.
    /// </summary>
    public UsersTextFilter? FullNameFilter { get; init; }

    /// <summary>
    /// Server-side sort key (Round 5). Null = the repository's default
    /// (FullName ascending, the pre-Round-5 order every existing caller
    /// relies on).
    /// </summary>
    public UsersSortBy? SortBy { get; init; }

    /// <summary>
    /// Sort direction for <see cref="SortBy"/> (Round 5). Ignored when
    /// SortBy is null.
    /// </summary>
    public bool SortDescending { get; init; }
}
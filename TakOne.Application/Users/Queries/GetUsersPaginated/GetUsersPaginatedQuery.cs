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
///   - <see cref="GroupName"/>: exact match (group names are short and
///     discrete — substring search is unnecessary).
///   - <see cref="IsActive"/>: tristate. Null = both, true = active only,
///     false = inactive only.
/// </summary>
public sealed class GetUsersPaginatedQuery
{
    public int PageNumber { get; init; } = 1;
    public int PageSize { get; init; } = 20;

    public string? SearchTerm { get; init; }
    public string? GroupName { get; init; }

    /// <summary>
    /// Tristate activity filter. Null = both, true = active only,
    /// false = inactive only.
    /// </summary>
    public bool? IsActive { get; init; }
}
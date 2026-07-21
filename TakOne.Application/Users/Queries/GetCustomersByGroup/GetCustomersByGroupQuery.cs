using TakOne.Application.Common.Authorization;
using TakOne.Application.Users.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Queries.GetCustomersByGroup;

/// <summary>
/// Loads ALL users in a given customer group. Returns a flat list of
/// <see cref="UserListItemDto"/> (NOT paginated — group membership is small).
///
/// USED BY:
///   - The manager's "view customers in group X" dashboard.
///   - The admin's "manage group X" page.
///
/// AUTHORIZATION MODEL:
///   - Admin / Manager: may call this query. Sees GroupName on each DTO.
///   - Employee: may call this query (they need it to pick a customer for
///     the "create sale on behalf of" flow). GroupName is stripped.
///   - Customer / ReadOnly: NOT allowed.
///
/// The handler enforces all three rules.
///
/// WHY NOT PAGINATED:
///   Group sizes in TakOne are expected to be tens of customers. A single
///   page is sufficient. If a group ever grows to hundreds, we'll add a
///   paginated variant — but the current shop workflow expects the
///   employee to see the whole group at once so they can pick a customer
///   from a dropdown.
/// </summary>
public sealed class GetCustomersByGroupQuery
{
    /// <summary>
    /// Exact group name to filter by. Required — passing null or whitespace
    /// is rejected by the handler.
    /// </summary>
    public string GroupName { get; init; } = string.Empty;
}
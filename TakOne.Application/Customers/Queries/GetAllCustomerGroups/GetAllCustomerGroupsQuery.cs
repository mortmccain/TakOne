using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Customers.Queries.GetAllCustomerGroups;

/// <summary>
/// Returns all customer groups (optionally including inactive ones).
/// Used by:
///   - The ManageGroups page (full list with edit/delete actions)
///   - The CreateUser / EditUser / CreateProduct / SetProductPurchaseLimit
///     pages' group dropdowns (they need at least the active groups)
///
/// AUTHORIZATION:
///   Employee, Manager, Admin. Customers / ReadOnly never need to list
///   groups — group membership is internal staff data.
///
/// EMPTY RESULT:
///   An empty list is NORMAL — fresh installs have no groups yet. The UI
///   should show "No groups exist yet. Create one." with a button to the
///   CreateGroup page.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed class GetAllCustomerGroupsQuery
{
    /// <summary>
    /// Whether to include deactivated groups in the result. Default: false
    /// (only active groups, which is what dropdowns want). The ManageGroups
    /// page passes true to show the full list with deactivated rows greyed
    /// out + a "reactivate" action.
    /// </summary>
    public bool IncludeInactive { get; init; } = false;
}
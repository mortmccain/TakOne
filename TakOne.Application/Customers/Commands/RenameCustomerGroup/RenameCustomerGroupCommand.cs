using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Customers.Commands.RenameCustomerGroup;

/// <summary>
/// Renames an existing customer group.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// SEMANTICS:
///   - The new name must be unique (excluding the group being renamed).
///   - Renaming does NOT affect existing references (User.GroupId,
///     CustomerGroupPurchaseLimit.GroupId) — they continue to point at
///     the same group row.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record RenameCustomerGroupCommand(
    Guid GroupId,
    string NewName);
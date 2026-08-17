using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Customers.Commands.ActivateCustomerGroup;

/// <summary>
/// Reactivates a previously-deactivated customer group.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// SEMANTICS:
///   - Idempotent — activating an already-active group is a no-op.
///   - Reactivation does NOT auto-restore per-product count limits that
///     were removed while the group was inactive (those are a separate
///     concern, owned by the Product aggregate).
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record ActivateCustomerGroupCommand(Guid GroupId);

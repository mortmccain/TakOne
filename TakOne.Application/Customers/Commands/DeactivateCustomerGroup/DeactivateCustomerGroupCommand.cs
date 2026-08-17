using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Customers.Commands.DeactivateCustomerGroup;

/// <summary>
/// Soft-deletes a customer group by setting <c>IsActive = false</c>.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// SEMANTICS:
///   - Idempotent — deactivating an already-inactive group is a no-op.
///   - Deactivation does NOT remove the group row — it stays in the DB
///     so historical references (User.GroupId, Sale.CustomerId → User →
///     CustomerGroup) remain valid for audit/reporting.
///   - Deactivation does NOT reassign users — they continue to reference
///     the deactivated group. The handler logs the count of affected
///     active users (loaded via ICustomerGroupRepository.GetActiveUserCountAsync)
///     so the admin knows the impact. The UI should warn the admin
///     BEFORE issuing this command if the count is non-zero.
///   - Per-product count limits for the deactivated group are NOT
///     removed (they stay on the Product aggregate). This is correct —
///     reactivating the group should restore the limits.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record DeactivateCustomerGroupCommand(Guid GroupId);
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.ActivateSubCategory;

/// <summary>
/// Reactivates a deactivated SubCategory.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// NOTE ON CASCADE:
///   Activating a SubCategory does NOT reactivate its SubSubCategories —
///   those must be reactivated individually. This mirrors the parent
///   Category's Activate behavior: reactivating a parent shouldn't silently
///   bring back previously-deactivated children.
///
/// NOTE ON PARENT:
///   The domain allows activating a SubCategory even if its parent Category
///   is deactivated. This is intentional — the admin may want to prepare
///   the hierarchy before reactivating the parent. If you want to forbid
///   this, add a guard in the handler.
///
/// IDEMPOTENCY:
///   Activating an already-active SubCategory is a no-op.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record ActivateSubCategoryCommand(Guid CategoryId, Guid SubCategoryId);
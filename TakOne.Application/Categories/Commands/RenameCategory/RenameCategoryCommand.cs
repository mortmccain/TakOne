using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.RenameCategory;

/// <summary>
/// Renames an existing top-level Category.
///
/// AUTHORIZATION:
///   Manager, Admin. Same rationale as CreateCategory — renaming a category
///   affects every product listing under it.
///
/// NAME UNIQUENESS:
///   The new name must not collide with another Category's name. The handler
///   excludes the renamed Category's own Id from the uniqueness check, so
///   renaming to the same name (no-op rename) is allowed.
///
/// DEACTIVATED CATEGORIES:
///   The domain allows renaming a deactivated Category — there is no business
///   reason to forbid it, and allowing it means an admin can clean up names
///   before reactivating. If you want to forbid this, add a guard in the
///   handler (not the domain).
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record RenameCategoryCommand(Guid CategoryId, string NewName);
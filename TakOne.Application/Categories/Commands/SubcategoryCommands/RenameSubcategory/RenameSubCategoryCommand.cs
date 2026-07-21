using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.RenameSubCategory;

/// <summary>
/// Renames an existing SubCategory.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// AGGREGATE BOUNDARY:
///   SubCategory is an entity inside the Category aggregate — the handler
///   loads the parent Category (with hierarchy) and routes the rename through
///   <see cref="Domain.Categories.Entities.Category.RenameSubCategory"/>.
///   Direct mutations on SubCategory are not allowed.
///
/// NAME UNIQUENESS:
///   The new name must not collide with another SubCategory under the SAME
///   parent Category. Enforced by the aggregate (intra-aggregate invariant).
///   The handler does NOT need a separate uniqueness check.
///
/// IDEMPOTENCY:
///   Renaming to the same name is allowed — the aggregate excludes the
///   renamed SubCategory's own Id from the uniqueness check.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record RenameSubCategoryCommand(Guid CategoryId, Guid SubCategoryId, string NewName);
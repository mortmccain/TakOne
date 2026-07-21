using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.RenameSubSubCategory;

/// <summary>
/// Renames an existing SubSubCategory.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// AGGREGATE BOUNDARY:
///   Routes through
///   <see cref="Domain.Categories.Entities.Category.RenameSubSubCategory"/>
///   → SubCategory.RenameSubSubCategory. The handler never touches
///   SubSubCategory directly.
///
/// NAME UNIQUENESS:
///   The new name must not collide with another SubSubCategory under the
///   SAME parent SubCategory. Intra-aggregate invariant — enforced by
///   the aggregate, not the handler. Renaming to the same name is allowed
///   (the aggregate excludes the renamed entity's own Id).
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record RenameSubSubCategoryCommand
    (
    Guid CategoryId,
    Guid SubCategoryId,
    Guid SubSubCategoryId,
    string NewName
    );
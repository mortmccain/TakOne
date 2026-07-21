using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.CreateSubSubCategory;

/// <summary>
/// Adds a new SubSubCategory under an existing SubCategory.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// AGGREGATE BOUNDARY:
///   SubSubCategory is an entity inside the SubCategory entity, which is
///   itself inside the Category aggregate. The handler loads the parent
///   Category (with hierarchy) and routes through
///   <see cref="Domain.Categories.Entities.Category.AddSubSubCategory"/>
///   → SubCategory.AddSubSubCategory. Direct construction is not allowed.
///
/// NAME UNIQUENESS:
///   SubSubCategory names must be unique WITHIN their parent SubCategory
///   (case-insensitive). Intra-aggregate invariant — enforced by the
///   aggregate, not the handler.
///
/// PARENT MUST BE ACTIVE:
///   The aggregate throws if the parent Category or the parent SubCategory
///   is deactivated.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record CreateSubSubCategoryCommand(Guid CategoryId, Guid SubCategoryId, string Name);
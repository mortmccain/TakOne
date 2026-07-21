using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.ActivateSubSubCategory;

/// <summary>
/// Reactivates a deactivated SubSubCategory.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// NOTE ON PARENT:
///   The domain allows activating a SubSubCategory even if its parent
///   SubCategory or grandparent Category is deactivated. Same rationale
///   as ActivateSubCategory — admin may want to prepare the hierarchy
///   before reactivating the parents.
///
/// IDEMPOTENCY:
///   Activating an already-active SubSubCategory is a no-op.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record ActivateSubSubCategoryCommand
    (
    Guid CategoryId,
    Guid SubCategoryId,
    Guid SubSubCategoryId
    );
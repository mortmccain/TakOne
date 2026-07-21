using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.SubSubCategoryCommands.DeactivateSubSubCategory;

/// <summary>
/// Soft-deletes a SubSubCategory by setting IsActive = false.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// IDEMPOTENCY:
///   Deactivating an already-deactivated SubSubCategory is a no-op.
///
/// PRODUCTS THAT REFERENCE THIS SUBSUBCATEGORY:
///   Existing Products keep their SubSubCategoryId reference — the row
///   stays in the DB (soft delete). The shop view should filter out
///   products whose subsubcategory is deactivated; that's a query-side
///   concern.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record DeactivateSubSubCategoryCommand
    (
    Guid CategoryId,
    Guid SubCategoryId,
    Guid SubSubCategoryId
    );
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.DeactivateSubCategory;

/// <summary>
/// Soft-deletes a SubCategory by setting IsActive = false.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// CASCADE:
///   Deactivating a SubCategory ALSO deactivates all its SubSubCategories.
///   Domain-level cascade (not DB-level) — see
///   <see cref="Domain.Categories.Entities.Category.DeactivateSubCategory"/>.
///
/// IDEMPOTENCY:
///   Deactivating an already-deactivated SubCategory is a no-op.
///
/// PRODUCTS THAT REFERENCE THIS SUBCATEGORY:
///   Existing Products keep their SubCategoryId reference — the row stays
///   in the DB (soft delete). The shop view should filter out products
///   whose subcategory is deactivated; that's a query-side concern.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record DeactivateSubCategoryCommand(Guid CategoryId, Guid SubCategoryId);
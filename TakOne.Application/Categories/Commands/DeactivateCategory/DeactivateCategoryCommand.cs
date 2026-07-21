using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.DeactivateCategory;

/// <summary>
/// Soft-deletes a top-level Category by setting IsActive = false.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// CASCADE:
///   Deactivating a Category ALSO deactivates all its SubCategories and
///   SubSubCategories. This is a domain-level cascade (not DB-level), so
///   the aggregate stays in control of the hierarchy. See
///   <see cref="Domain.Categories.Entities.Category.Deactivate"/>.
///
/// IDEMPOTENCY:
///   Deactivating an already-deactivated Category is a no-op. We still
///   persist (idempotent no-op → SaveChangesAsync is a null-op round-trip).
///
/// PRODUCTS THAT REFERENCE THIS CATEGORY:
///   Existing Products keep their CategoryId reference — the row stays in
///   the DB (soft delete). The shop view should filter out products whose
///   category is deactivated; that's a query-side concern, not this
///   command's responsibility.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record DeactivateCategoryCommand(Guid CategoryId);
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.ActivateCategory;

/// <summary>
/// Reactivates a deactivated top-level Category.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// NOTE ON CASCADE:
///   Activating a Category does NOT reactivate its SubCategories or
///   SubSubCategories — those must be reactivated individually. This is
///   intentional: reactivating a parent shouldn't silently bring back
///   previously-deactivated children. See <see cref="Domain.Categories.Entities.Category.Activate"/>
///   for the rationale.
///
/// IDEMPOTENCY:
///   Activating an already-active Category is a no-op (the domain method
///   unconditionally sets IsActive = true). We still persist; EF Core will
///   just not detect any changes and SaveChangesAsync is a null-op round-trip.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record ActivateCategoryCommand(Guid CategoryId);
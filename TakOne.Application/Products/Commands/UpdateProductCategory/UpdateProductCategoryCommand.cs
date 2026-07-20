using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Products.Commands.UpdateProductCategory;

/// <summary>
/// Updates a Product's category assignment. Pass null for SubCategoryId /
/// SubSubCategoryId to clear them (move the product back up the hierarchy).
///
/// AUTHORIZATION:
///   Employee, Manager, Admin.
///
/// CROSS-AGGREGATE HIERARCHY:
///   The handler validates:
///     - CategoryId exists
///     - If SubCategoryId provided, it belongs to CategoryId
///     - If SubSubCategoryId provided, it belongs to SubCategoryId
///   These checks use the dedicated ICategoryRepository methods (efficient
///   SQL EXISTS queries — no need to load the whole Category aggregate).
///
/// NOT ENFORCED HERE:
///   Whether the new category is "active" (not soft-deleted). That's the
///   Category aggregate's concern — if soft-deletion should block product
///   reassignment, the ICategoryRepository.ExistsAsync implementation should
///   filter out soft-deleted categories. (See Infrastructure layer, step 7.)
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record UpdateProductCategoryCommand(
    Guid ProductId,
    Guid CategoryId,
    Guid? SubCategoryId,
    Guid? SubSubCategoryId);
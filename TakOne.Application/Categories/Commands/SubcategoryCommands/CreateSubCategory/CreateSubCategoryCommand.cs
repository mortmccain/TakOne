using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.SubcategoryCommands.CreateSubCategory;

/// <summary>
/// Adds a new SubCategory under an existing top-level Category.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// AGGREGATE BOUNDARY:
///   SubCategory is an entity INSIDE the Category aggregate — it has no
///   separate repository. The handler loads the parent Category (with
///   hierarchy) and calls <see cref="Domain.Categories.Entities.Category.AddSubCategory"/>.
///   EF Core's change tracker will detect the new entity and persist it
///   on SaveChangesAsync.
///
/// NAME UNIQUENESS:
///   SubCategory names must be unique WITHIN their parent Category
///   (case-insensitive). This is an intra-aggregate invariant, enforced
///   by the aggregate itself — the domain throws DomainException if the
///   name clashes. The handler does NOT need a separate uniqueness check
///   at the application layer.
///
/// PARENT MUST BE ACTIVE:
///   The aggregate throws if the parent Category is deactivated. The
///   handler doesn't pre-check this — letting the domain throw keeps
///   the rule in one place.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record CreateSubCategoryCommand(Guid CategoryId, string Name);
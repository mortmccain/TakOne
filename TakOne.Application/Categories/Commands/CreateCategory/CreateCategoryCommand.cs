using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Categories.Commands.CreateCategory;

/// <summary>
/// Creates a new top-level Category in the catalog.
///
/// AUTHORIZATION:
///   Manager, Admin. Employees and below cannot create categories — categories
///   structure the whole shop and changing them affects every product listing.
///   Role check enforced by <see cref="Common.Middlewares.AuthorizationMiddleware"/>
///   via <see cref="RequireRolesAttribute"/>.
///
/// NAME UNIQUENESS:
///   Category names must be unique across the catalog (case-insensitive).
///   Enforced by the handler via <see cref="Common.Interfaces.ICategoryRepository.NameExistsAsync"/>,
///   and backed by a unique index in the database as a hard guarantee against
///   race conditions.
///
/// DOMAIN EVENT:
///   On success, <see cref="Domain.Categories.Events.CategoryCreatedDomainEvent"/>
///   is raised. It is dispatched after SaveChangesAsync by the Infrastructure
///   layer's domain-event dispatcher interceptor.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record CreateCategoryCommand(string Name);
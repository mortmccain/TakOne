using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Products.Commands.SetProductPurchaseLimit;

/// <summary>
/// Sets (adds or replaces) the per-group purchase limit on a Product for the
/// given customer group. Because <c>CustomerGroupPurchaseLimit</c> is a value
/// object (immutable), "changing" a limit means the Product aggregate replaces
/// the old instance with a new one internally.
///
/// AUTHORIZATION:
///   Employee, Manager, Admin.
///
/// SEMANTICS:
///   - If a limit already exists for the given group, it is overwritten.
///   - If no limit exists, a new one is added.
///   - The limit applies to every customer whose <c>User.GroupName</c> matches
///     <see cref="GroupName"/>. Matching is case-sensitive.
///
/// TO REMOVE A LIMIT:
///   Use <see cref="RemoveProductPurchaseLimitCommand"/> — passing Limit = 0
///   here would throw (the domain requires Limit ≥ 1).
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record SetProductPurchaseLimitCommand(
    Guid ProductId,
    string GroupName,
    int Limit);
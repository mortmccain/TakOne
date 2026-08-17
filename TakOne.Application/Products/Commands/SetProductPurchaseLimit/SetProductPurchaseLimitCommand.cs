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
///   - The limit applies to every customer whose <c>User.GroupId</c> matches
///     <see cref="GroupId"/>.
///
/// TO REMOVE A LIMIT:
///   Use <see cref="RemoveProductPurchaseLimitCommand"/> — passing Limit = 0
///   here would throw (the domain requires Limit ≥ 1).
///
/// SALARY FEATURE (Step 3):
///   Takes <c>GroupId</c> (Guid) instead of <c>GroupName</c> (string). The
///   UI must select from existing groups via the
///   <c>GetAllCustomerGroupsQuery</c> — free-text group names are no longer
///   supported. This guarantees every limit references a real
///   <c>CustomerGroup</c> row, which is required for currency matching
///   (a customer can only buy products priced in their group's salary currency).
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record SetProductPurchaseLimitCommand(
    Guid ProductId,
    Guid GroupId,
    int Limit);
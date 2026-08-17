using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Products.Commands.RemoveProductPurchaseLimit;

/// <summary>
/// Removes the per-group purchase limit on a Product for the given customer
/// group, if one exists. Idempotent — if no limit exists for the group, the
/// command succeeds without doing anything.
///
/// AUTHORIZATION:
///   Employee, Manager, Admin.
///
/// POST-CONDITION:
///   After this command succeeds, customers in the given group can buy the
///   product with no quantity limit. If a different limit should apply
///   instead, use <see cref="SetProductPurchaseLimitCommand"/> to overwrite
///   rather than remove.
///
/// SALARY FEATURE (Step 3):
///   Takes <c>GroupId</c> (Guid) instead of <c>GroupName</c> (string).
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record RemoveProductPurchaseLimitCommand(
    Guid ProductId,
    Guid GroupId);
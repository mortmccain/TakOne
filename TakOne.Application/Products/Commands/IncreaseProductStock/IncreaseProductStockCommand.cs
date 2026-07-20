using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Products.Commands.IncreaseProductStock;

/// <summary>
/// Increases a Product's stock by the given quantity. Used when restocking.
///
/// AUTHORIZATION:
///   Employee, Manager, Admin.
///
/// NOT FOR DECREASING STOCK:
///   Stock is decreased automatically when a Sale is approved (see
///   <c>ApproveSaleCommandHandler</c>). There is no manual
///   DecreaseProductStockCommand — manual corrections for damaged/lost
///   inventory should use the dedicated stock-adjustment flow when that gets
///   built (separate from this command). Mixing manual decreases with
///   sale-driven decreases in the same handler would muddle the audit trail.
///
/// NEGATIVE QUANTITIES:
///   Rejected by the validator and by the domain. Use a positive quantity;
///   the resulting stock is current + quantity.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record IncreaseProductStockCommand(
    Guid ProductId,
    int Quantity);
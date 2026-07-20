using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.CancelSale;

/// <summary>
/// Cancels a Sale. Reachable from Pending or Approved (NOT from Draft — use
/// <see cref="DeleteDraftSaleCommand"/> for drafts; NOT from Invoiced —
/// issue a credit note in a separate flow).
///
/// AUTHORIZATION:
///   Employee, Manager, Admin. Customers cannot cancel their own submitted
///   sales — they must contact staff. (This matches the rule that customers
///   cannot approve their own sales.)
///
/// STOCK SIDE-EFFECT:
///   If the sale was in Approved status (meaning stock was decremented at
///   Approve time), cancellation RESTORES the stock for each line item.
///   If the sale was in Pending status (stock not yet decremented), no
///   restoration happens.
///
/// REASON:
///   A cancellation reason is required (enforced by validator and by the
///   domain). Used for audit trail — "who cancelled this and why?".
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record CancelSaleCommand(
    Guid SaleId,
    string Reason);
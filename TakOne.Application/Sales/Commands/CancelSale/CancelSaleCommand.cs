using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.CancelSale;

/// <summary>
/// Cancels a Pending or Approved Sale. Terminal state.
/// Cannot cancel a Draft (use DeleteDraftSaleCommand) or an Invoiced sale.
///
/// Staff-only: Employee or Manager.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager)]
public sealed record CancelSaleCommand(
    Guid SaleId,
    string Reason);
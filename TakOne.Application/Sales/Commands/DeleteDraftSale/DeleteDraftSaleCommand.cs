using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.DeleteDraftSale;

/// <summary>
/// Hard-deletes a Draft Sale. Drafts are disposable carts — we don't keep
/// audit trail for people adding/removing items from their cart without
/// submitting. Only Draft sales can be deleted this way.
///
/// For non-Draft sales (Pending/Approved/Invoiced/Cancelled), use
/// <see cref="CancelSaleCommand"/> — the domain Sale.Cancel() throws for
/// Drafts precisely because drafts go through this command instead.
///
/// OWNERSHIP:
///   Only the sale's creator can delete their draft. Customers delete their
///   own carts; staff delete drafts they're building on behalf of customers.
/// </summary>
[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record DeleteDraftSaleCommand(Guid SaleId);
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.MarkAsInvoiced;

/// <summary>
/// Transitions a Sale from Approved to Invoiced. "Invoiced" means physical
/// handover is complete. Invoiced is a terminal state — the sale cannot be
/// cancelled after this point (a credit note flow would be used instead).
///
/// AUTHORIZATION:
///   Employee, Manager, Admin.
///
/// STOCK SIDE-EFFECT:
///   None. Stock was already decremented at Approve time. This command just
///   marks the sale as fully delivered.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record MarkAsInvoicedCommand(Guid SaleId);
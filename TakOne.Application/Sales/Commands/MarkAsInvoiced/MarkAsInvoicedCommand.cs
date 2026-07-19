using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.MarkSaleAsInvoiced;

/// <summary>
/// Marks an Approved Sale as invoiced (physical handover complete).
/// Terminal state — the sale cannot be cancelled after this.
///
/// Staff-only: Employee or Manager.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager)]
public sealed record MarkAsInvoicedCommand(Guid SaleId);
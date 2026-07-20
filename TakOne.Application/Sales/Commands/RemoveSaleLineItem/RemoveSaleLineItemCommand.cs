using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.RemoveSaleLineItem;

/// <summary>
/// Removes a line item from a Draft Sale. Only callable on Draft sales.
/// Line numbers on the sale are stable (deleting line 2 does NOT renumber
/// line 3) — see the Sale aggregate's GetNextLineNumber for the rationale.
/// </summary>
[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record RemoveSaleLineItemCommand
    (
    Guid SaleId,
    Guid LineItemId
    );
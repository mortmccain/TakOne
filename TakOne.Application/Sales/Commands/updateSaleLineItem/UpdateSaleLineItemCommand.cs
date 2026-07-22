using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.UpdateSaleLineItem;

/// <summary>
/// Replaces the quantity of an existing line item on a Draft Sale.
///
/// Unlike AddItemToSale (which increments), this SETS the line's quantity to
/// <see cref="Quantity"/>. Pass Quantity = 0 to remove the line — actually,
/// don't: use <see cref="RemoveSaleLineItemCommand"/> instead. The validator
/// enforces Quantity ≥ 1.
///
/// The handler re-resolves the per-group purchase limit at update time,
/// because either the customer's group or the product's limit may have
/// changed since the line was first added.
/// </summary>
[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record UpdateSaleLineItemCommand(
    Guid SaleId,
    Guid LineItemId,
    int Quantity);
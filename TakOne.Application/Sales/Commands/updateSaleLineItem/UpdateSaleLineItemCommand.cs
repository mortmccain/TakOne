using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.UpdateSaleLineItem;

[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record UpdateSaleLineItemCommand
    (
    Guid SaleId,
    Guid LineItemId,
    int Quantity
    );
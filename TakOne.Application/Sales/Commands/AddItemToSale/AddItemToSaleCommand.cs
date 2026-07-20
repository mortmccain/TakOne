using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.AddItemToSale;

[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record AddItemToSaleCommand
    (
    Guid SaleId,
    Guid ProductId,
    int Quantity
    );

using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.CreateSale;

// Everyone authenticated can create a sale (customer self-buy, or staff on behalf).
[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record CreateSaleCommand
    (
    string CustomerWorkerId,
    IReadOnlyList<CreateSaleItem> Items
    );

public sealed record CreateSaleItem(Guid ProductId, int Quantity);
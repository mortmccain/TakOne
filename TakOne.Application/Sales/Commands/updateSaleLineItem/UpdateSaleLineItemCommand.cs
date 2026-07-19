namespace TakOne.Application.Sales.Commands.updateSaleLineItem;

public sealed record UpdateSaleLineItemCommand
    (
    Guid SaleId,
    Guid LineItemId,
    int NewQuantity
    );

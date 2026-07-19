namespace TakOne.Application.Sales.Commands.RemoveSaleLineItem;

public sealed record RemoveSaleLineItemCommand
    (
    Guid SaleId,
    Guid LineItemId
    );
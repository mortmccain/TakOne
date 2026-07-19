namespace TakOne.Application.Sales.Commands.AddItemToSale;

/// <summary>
/// Adds a product to the current user's Draft Sale, or increments the
/// quantity if the product is already on the sale.
///
/// The handler loads the Product to get its current price and to look up
/// the per-group purchase limit for the current user. The limit is then
/// enforced by the Sale aggregate's AddLineItem method.
/// </summary>
public sealed record AddItemToSaleCommand
    (
    Guid SaleId,
    Guid ProductId,
    int Quantity
    );
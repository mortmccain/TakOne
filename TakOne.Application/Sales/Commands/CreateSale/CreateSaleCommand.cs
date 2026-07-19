namespace TakOne.Application.Sales.Commands.CreateSale;

/// <summary>
/// Creates a new Sale in Draft status for the current user.
/// Returns the new Sale's Id on success.
///
/// The current user becomes the CustomerId and the CreatedByUserId.
/// Line items are added via <see cref="AddItemToSaleCommand"/>.
/// </summary>
public sealed record CreateSaleCommand;
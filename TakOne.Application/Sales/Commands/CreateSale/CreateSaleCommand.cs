namespace TakOne.Application.Sales.Commands.CreateSale;

/// <summary>
/// Command to create a new Sale.
/// Returns the ID of the newly created Sale on success.
/// </summary>
public sealed class CreateSaleCommand
{
    // all properties are init because commands are immutable
    public Guid CustomerId { get; init; }
    public Guid CreatedByUserId { get; init; }
    public string CreatedByName { get; init; } = string.Empty;
    public List<LineItem> Items { get; init; } = new();

    // line item sealed class is an input DTO for line items

    /// <summary>
    /// Nested class representing a single line item within the create command.
    /// Defined here because it only exists in the context of this command.
    /// </summary>
    public sealed class LineItem
    {
        public Guid ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string? SKU { get; init; }
        public int Quantity { get; init; }
        public decimal UnitPriceAmount { get; init; }
        public string Currency { get; init; } = "IRR";
    }
}

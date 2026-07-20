using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Read-side DTO for a single line on a Sale.
/// Used in <see cref="SaleDto"/> and <see cref="SaleListItemDto"/>.
/// </summary>
public sealed class SaleLineItemDto
{
    public Guid Id { get; init; }
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public MoneyDto UnitPrice { get; init; } = new();
    public int LineNumber { get; init; }
    public MoneyDto GrossTotal { get; init; } = new();
}
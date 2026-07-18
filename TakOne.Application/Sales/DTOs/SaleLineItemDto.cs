using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

// DTO for output
public sealed class SaleLineItemDto
{
    public Guid Id { get; init; }
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public MoneyDto UnitPrice { get; init; } = null!;
    public MoneyDto Total { get; init; } = null!;
    public MoneyDto LineTotal { get; init; } = null!;
    public int LineNumber { get; init; }
}

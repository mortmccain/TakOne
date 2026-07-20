using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Read-side DTO for a single line on a Sale.
///
/// UnitPrice and GrossTotal are exposed as <see cref="MoneyDto"/>. The Gross
/// total is computed by the domain <c>SaleLineItem.GrossTotal</c> property
/// (Quantity × UnitPrice) and projected into a MoneyDto here.
/// </summary>
public sealed class SaleLineItemDto
{
    public Guid Id { get; init; }
    public int LineNumber { get; init; }
    public Guid ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public int Quantity { get; init; }
    public MoneyDto UnitPrice { get; init; } = new();
    public MoneyDto GrossTotal { get; init; } = new();
}
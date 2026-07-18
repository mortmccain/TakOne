using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Lightweight DTO for displaying sales in a paginated list.
/// Contains only the fields needed for the list view — not the full line items.
/// </summary>
public sealed class SaleListItemDto
{
    public Guid Id { get; init; }
    public string SaleNumber { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public MoneyDto Total { get; init; } = null!;
    public DateTime CreatedAtUtc { get; init; }
    public Guid CreatedByUserId { get; init; }        // needed for employee ownership check
    public string CreatedByName { get; init; } = string.Empty;  // shown in admin/manager view
}
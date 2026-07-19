using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Lightweight DTO for displaying sales in a paginated list.
/// Contains only the fields needed for the list view — not the full line items.
/// </summary>
/// <summary>
/// Read-side DTO for a Sale in a paginated list. Line items are NOT included.
/// </summary>
public sealed class SaleListItemDto
{
    public Guid Id { get; init; }
    public string SaleNumber { get; init; } = string.Empty;
    public Guid CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public string Currency { get; init; } = "IRR";
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
}
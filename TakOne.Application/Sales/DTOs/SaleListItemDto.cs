using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Lightweight DTO for displaying sales in a paginated list.
/// Contains only the fields needed for the list view — line items are NOT included.
/// </summary>
public sealed class SaleListItemDto
{
    public Guid Id { get; init; }
    public string SaleNumber { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public Guid CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public Guid CreatedByUserId { get; init; }
    public string CreatedByName { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? SubmittedAtUtc { get; init; }
    public DateTimeOffset? ApprovedAtUtc { get; init; }
    public MoneyDto Total { get; init; } = new();
}
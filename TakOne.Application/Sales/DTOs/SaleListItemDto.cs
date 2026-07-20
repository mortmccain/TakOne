using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

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
    public MoneyDto Total { get; init; } = new();
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
}

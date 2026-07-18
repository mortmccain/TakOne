using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

public sealed class SaleDto
{
    public Guid Id { get; init; }
    public string SaleNumber { get; init; } = string.Empty;
    public Guid CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    // --- Creator ---
    public Guid CreatedByUserId { get; init; }
    public string CreatedByName { get; init; } = string.Empty;

    // --- Shipping ---

    // --- Financial ---
    public MoneyDto Total { get; init; } = null!;

    // --- Timestamps ---
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
    public Guid? ApprovedByUserId { get; init; }
    public DateTime? InvoicedAtUtc { get; init; }
    public DateTime? CancelledAtUtc { get; init; }
    public string? CancellationReason { get; init; }

    // --- Line Items ---
    public List<SaleLineItemDto> LineItems { get; init; } = new();
}
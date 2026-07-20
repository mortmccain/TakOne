using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Read-side DTO for a full Sale, including its line items.
///
/// Money is exposed as <see cref="MoneyDto"/> (Amount + Currency) so the API
/// layer doesn't need to know about the domain <c>Money</c> value object.
///
/// Audit fields that aren't set yet in the sale's lifecycle are exposed as
/// nullable (ApprovedByUserId, InvoicedByUserId, CancelledByUserId). The
/// domain stores them as non-nullable Guids (Guid.Empty until set); the DTO
/// projects Guid.Empty → null so consumers don't have to know the convention.
/// </summary>
public sealed class SaleDto
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
    public DateTimeOffset? InvoicedAtUtc { get; init; }
    public DateTimeOffset? CancelledAtUtc { get; init; }
    public string? CancellationReason { get; init; }

    /// <summary>
    /// The user who approved the sale. Null until the sale reaches Approved
    /// (or beyond). Invariant: customers never see this value in the UI — it
    /// exists for staff-side auditing only.
    /// </summary>
    public Guid? ApprovedByUserId { get; init; }

    /// <summary>
    /// The user who marked the sale as invoiced. Null until the sale is Invoiced.
    /// </summary>
    public Guid? InvoicedByUserId { get; init; }

    /// <summary>
    /// The user who cancelled the sale. Null unless the sale is Cancelled.
    /// Used for investigation ("who cancelled this order and why?").
    /// </summary>
    public Guid? CancelledByUserId { get; init; }
    public MoneyDto Total { get; init; } = new();
    public List<SaleLineItemDto> LineItems { get; init; } = new();
}
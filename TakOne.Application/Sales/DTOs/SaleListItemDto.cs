using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Lightweight DTO for displaying sales in a paginated list.
/// Contains only the fields needed for the list view — line items are NOT included.
///
/// SALE NUMBER NULLABILITY (B2 deferred-allocation design):
///   <see cref="SaleNumber"/> is null when the sale is still a Draft. Use
///   <see cref="DisplayNumber"/> for UI display and for the grid's filter —
///   it returns the real SaleNumber for submitted sales, or a pseudo-id
///   (<c>DRAFT-{Guid[0..8]}</c>) for drafts, so drafts are searchable.
/// </summary>
public sealed class SaleListItemDto
{
    public Guid Id { get; init; }

    /// <summary>
    /// The permanent SaleNumber, or null when the sale is a Draft.
    /// Use <see cref="DisplayNumber"/> for UI display.
    /// </summary>
    public string? SaleNumber { get; init; }

    /// <summary>
    /// Display-safe identifier: real SaleNumber for submitted sales, or
    /// <c>DRAFT-{first 8 hex chars of Id}</c> for drafts. Never null.
    /// </summary>
    public string DisplayNumber =>
        SaleNumber ?? $"DRAFT-{Id.ToString()[..8].ToUpperInvariant()}";

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
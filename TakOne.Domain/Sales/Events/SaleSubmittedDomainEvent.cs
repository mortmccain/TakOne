using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Sales.Events;

/// <summary>
/// Raised when a Sale transitions from Draft to Pending status.
/// This is the moment the customer submits their cart for staff review.
/// The submitter is always the sale's creator (CreatedByUserId) — a sale
/// cannot be submitted by anyone other than the customer who created it.
/// </summary>
public sealed class SaleSubmittedDomainEvent : BaseDomainEvent
{
    public Guid SaleId { get; }
    public Guid CustomerId { get; }
    public Money Total { get; }
    public DateTime SubmittedAtUtc { get; }
    public Guid CreatedByUserId { get; }

    public SaleSubmittedDomainEvent
        (
        Guid saleId,
        Guid customerId,
        Money total,
        Guid createdByUserId)
    {
        SaleId = saleId;
        CustomerId = customerId;
        Total = total;
        SubmittedAtUtc = DateTime.UtcNow;
        CreatedByUserId = createdByUserId;
    }
}
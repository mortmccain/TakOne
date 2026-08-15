using TakOne.Domain.Sales.ValueObjects;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Sales.Events;

/// <summary>
/// Raised when a Sale transitions from Draft to Pending status.
/// This is the moment the customer submits their cart for staff review.
/// The submitter is always the sale's creator (CreatedByUserId) — a sale
/// cannot be submitted by anyone other than the customer who created it.
///
/// SALE NUMBER (B2 design):
///   The permanent SaleNumber is allocated by the application layer immediately
///   before the Sale.Submit() call, and is included in this event. Unlike
///   SaleCreatedDomainEvent (where SaleNumber is null because the draft had no
///   number), this event's SaleNumber is ALWAYS NON-NULL — by the time Submit()
///   raises this event, the number has been assigned to the Sale aggregate.
///   Consumers that need a displayable identifier for a submitted sale can use
///   this value directly.
/// </summary>
public sealed class SaleSubmittedDomainEvent : BaseDomainEvent
{
    public Guid SaleId { get; }
    public Guid CustomerId { get; }
    public Money Total { get; }
    public SaleNumber SaleNumber { get; }
    public DateTime SubmittedAtUtc { get; }
    public Guid CreatedByUserId { get; }

    public SaleSubmittedDomainEvent
        (
        Guid saleId,
        Guid customerId,
        Money total,
        SaleNumber saleNumber,
        Guid createdByUserId)
    {
        SaleId = saleId;
        CustomerId = customerId;
        Total = total;
        SaleNumber = saleNumber;
        SubmittedAtUtc = DateTime.UtcNow;
        CreatedByUserId = createdByUserId;
    }
}
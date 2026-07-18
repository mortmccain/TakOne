using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Sales.Events;

/// <summary>
/// Raised when a Sale transitions from Pending to Approved status.
/// This is a critical business event — it means the company has committed to fulfilling the order.
/// </summary>
public sealed class SaleApprovedDomainEvent : BaseDomainEvent
{
    public Guid SaleId { get; }
    public Guid CustomerId { get; }
    public Money Total { get; }
    public Guid ApprovedByUserId { get; }
    public DateTime ApprovedAtUtc { get; }

    public SaleApprovedDomainEvent(
        Guid saleId,
        Guid customerId,
        Money total,
        Guid approvedByUserId)
    {
        SaleId = saleId;
        CustomerId = customerId;
        Total = total;
        ApprovedByUserId = approvedByUserId;
        ApprovedAtUtc = DateTime.UtcNow;
    }
}

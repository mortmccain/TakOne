using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Sales.Events;

/// <summary>
/// Raised when a Sale is cancelled. The sale is terminated and no further actions can be taken on it.
/// </summary>
public sealed class SaleCancelledDomainEvent : BaseDomainEvent
{
    public Guid SaleId { get; }
    public Guid CancelledByUserId { get; }
    public string Reason { get; }
    public DateTime CancelledAtUtc { get; }

    public SaleCancelledDomainEvent(
        Guid saleId,
        Guid cancelledByUserId,
        string reason)
    {
        SaleId = saleId;
        CancelledByUserId = cancelledByUserId;
        Reason = reason;
        CancelledAtUtc = DateTime.UtcNow;
    }
}

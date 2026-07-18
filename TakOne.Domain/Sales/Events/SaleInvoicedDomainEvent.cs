using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Sales.Events;

/// <summary>
/// Raised when a Sale transitions from Shipped to Invoiced.
/// </summary>
public sealed class SaleInvoicedDomainEvent : BaseDomainEvent
{
    public Guid Id { get; }

    public SaleInvoicedDomainEvent(Guid id)
    {
        Id = id;
    }
}
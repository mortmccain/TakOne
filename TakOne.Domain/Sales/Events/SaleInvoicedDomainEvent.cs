using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Sales.Events;

/// <summary>
/// Raised when a Sale transitions from Approved to Invoiced.
/// "Invoiced" means the physical handover is complete and the sale is finalized.
/// </summary>
public sealed class SaleInvoicedDomainEvent : BaseDomainEvent
{
    public Guid SaleId { get; }
    public Guid InvoicedByUserId { get; }
    public DateTime InvoicedAtUtc { get; }

    public SaleInvoicedDomainEvent(Guid saleId, Guid invoicedByUserId)
    {
        SaleId = saleId;
        InvoicedByUserId = invoicedByUserId;
        InvoicedAtUtc = DateTime.UtcNow;
    }
}
using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Sales.Events;

/// <summary>
/// Raised when a line item is removed from a Sale.
/// </summary>
public sealed class SaleLineItemRemovedDomainEvent : BaseDomainEvent
{
    public Guid SaleId { get; }
    public Guid LineItemId { get; }
    public Guid ProductId { get; }
    public int RemovedLineNumber { get; }

    public SaleLineItemRemovedDomainEvent(
        Guid saleId,
        Guid lineItemId,
        Guid productId,
        int removedLineNumber)
    {
        SaleId = saleId;
        LineItemId = lineItemId;
        ProductId = productId;
        RemovedLineNumber = removedLineNumber;
    }
}

using TakOne.Domain.Sales.ValueObjects;
using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Sales.Events;

/// <summary>
/// Raised when a new Sale is created (in Draft status).
/// Handlers can use this to send notifications, initialize audit trails, etc.
/// </summary>
public sealed class SaleCreatedDomainEvent : BaseDomainEvent
{
    public Guid SaleId { get; }
    public SaleNumber SaleNumber { get; }
    public Guid CustomerId { get; }
    public string CustomerName { get; }
    public DateTime CreatedAtUtc { get; }
    public Guid CreatedByUserId { get; }
    public string CreatedByName { get; }

    public SaleCreatedDomainEvent
        (
        Guid saleId,
        Guid buyerId,
        string buyerName,
        SaleNumber saleNumber,
        DateTime createdAtUtc,
        Guid createdByUserId,
        string createdByName
        )
    {
        SaleId = saleId;
        CustomerId = buyerId;
        CustomerName = buyerName;
        SaleNumber = saleNumber;
        CreatedAtUtc = createdAtUtc;
        CreatedByUserId = createdByUserId;
        CreatedByName = createdByName;

    }
}
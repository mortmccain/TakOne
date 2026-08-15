using TakOne.Domain.Sales.ValueObjects;
using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Sales.Events;

/// <summary>
/// Raised when a new Sale is created (in Draft status).
/// Handlers can use this to send notifications, initialize audit trails, etc.
///
/// NOTE: <see cref="SaleNumber"/> is NULL at creation time under the B2 deferred-allocation
/// design — the permanent sale number is only assigned when the sale is submitted
/// (see <see cref="Sale.Submit"/>). Consumers that need a displayable identifier
/// for a draft should use the sale's Guid Id (or a derived pseudo-id) instead.
/// </summary>
public sealed class SaleCreatedDomainEvent : BaseDomainEvent
{
    public Guid SaleId { get; }
    public SaleNumber? SaleNumber { get; }
    public Guid CustomerId { get; }
    public string CustomerName { get; }
    public DateTime CreatedAtUtc { get; }
    public Guid CreatedByUserId { get; }
    public string CreatedByName { get; }

    public SaleCreatedDomainEvent
        (
        Guid saleId,
        Guid customerId,
        string customerName,
        SaleNumber? saleNumber,
        DateTime createdAtUtc,
        Guid createdByUserId,
        string createdByName
        )
    {
        SaleId = saleId;
        CustomerId = customerId;
        CustomerName = customerName;
        SaleNumber = saleNumber;
        CreatedAtUtc = createdAtUtc;
        CreatedByUserId = createdByUserId;
        CreatedByName = createdByName;

    }
}
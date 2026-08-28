using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Products.Events;

/// <summary>
/// Raised when a previously-deactivated Product is re-activated via
/// <c>Product.Activate()</c>. Subscribers can use this to re-list the
/// product in shop catalogs and re-index it for search.
/// </summary>
public sealed class ProductActivatedDomainEvent : BaseDomainEvent
{
    public Guid ProductId { get; }
    public int StockQuantityAtActivation { get; }

    public ProductActivatedDomainEvent(
        Guid productId,
        int stockQuantityAtActivation)
    {
        ProductId = productId;
        StockQuantityAtActivation = stockQuantityAtActivation;
    }
}

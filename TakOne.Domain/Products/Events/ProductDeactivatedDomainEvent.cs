using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Products.Events;

/// <summary>
/// Raised when a Product is deactivated via <c>Product.Deactivate()</c>.
/// Deactivation is a soft-delete: the row is retained for audit, but the
/// product is hidden from shop listings and cannot be added to carts.
/// Subscribers can use this event to invalidate shop caches and remove
/// the product from search results.
/// </summary>
public sealed class ProductDeactivatedDomainEvent : BaseDomainEvent
{
    public Guid ProductId { get; }
    public int StockQuantityBeforeDeactivation { get; }

    public ProductDeactivatedDomainEvent(
        Guid productId,
        int stockQuantityBeforeDeactivation)
    {
        ProductId = productId;
        StockQuantityBeforeDeactivation = stockQuantityBeforeDeactivation;
    }
}

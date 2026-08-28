using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Products.Events;

/// <summary>
/// Raised when a new <see cref="Entities.Product"/> is created via the
/// <see cref="Entities.Product.Create"/> factory. Subscribers can use this
/// to invalidate a catalog cache, push a search-index entry, or seed
/// default purchase limits.
/// </summary>
public sealed class ProductCreatedDomainEvent : BaseDomainEvent
{
    public Guid ProductId { get; }
    public string Name { get; }
    public Guid CategoryId { get; }
    public Money Price { get; }
    public int InitialStockQuantity { get; }

    public ProductCreatedDomainEvent(
        Guid productId,
        string name,
        Guid categoryId,
        Money price,
        int initialStockQuantity)
    {
        ProductId = productId;
        Name = name;
        CategoryId = categoryId;
        Price = price;
        InitialStockQuantity = initialStockQuantity;
    }
}

using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Products.Events;

/// <summary>
/// Raised when a Product's basic descriptive fields are updated via
/// <see cref="Entities.Product.UpdateDetails"/>. Subscribers can use
/// this to invalidate catalog caches and refresh search index entries.
/// Carries both the previous and new values so audit subscribers can
/// reconstruct the change history without re-querying the aggregate.
/// </summary>
public sealed class ProductDetailsUpdatedDomainEvent : BaseDomainEvent
{
    public Guid ProductId { get; }
    public string PreviousName { get; }
    public string NewName { get; }
    public Money PreviousPrice { get; }
    public Money NewPrice { get; }

    public ProductDetailsUpdatedDomainEvent(
        Guid productId,
        string previousName,
        string newName,
        Money previousPrice,
        Money newPrice)
    {
        ProductId = productId;
        PreviousName = previousName;
        NewName = newName;
        PreviousPrice = previousPrice;
        NewPrice = newPrice;
    }
}

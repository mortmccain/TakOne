using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Products.Events;

/// <summary>
/// Raised whenever a Product's stock quantity changes via any of the
/// Product's stock-mutation methods (<c>IncreaseStock</c>,
/// <c>DecreaseStock</c>, <c>SetStock</c>, <c>AdjustStockTo</c>, or
/// <c>Deactivate</c>). Subscribers can use this for audit trails,
/// low-stock alerts, or to invalidate caches.
/// </summary>
/// <remarks>
/// The event carries BOTH the previous and new quantities so that
/// subscribers can determine the direction of the change (increase vs
/// decrease) without re-querying the aggregate. This is critical for
/// audit-trail reconstruction — the previous quantity is no longer
/// available after the mutation completes.
/// </remarks>
public sealed class ProductStockAdjustedDomainEvent : BaseDomainEvent
{
    public Guid ProductId { get; }
    public int PreviousQuantity { get; }
    public int NewQuantity { get; }

    /// <summary>
    /// The reason for the adjustment, if known. The domain does not
    /// enforce a non-null reason — callers pass a human-readable string
    /// when they have one (e.g. "restock", "sale approved", "manual
    /// adjustment", "deactivation"). Subscribers can filter on this
    /// for audit categorization.
    /// </summary>
    public string? Reason { get; }

    public ProductStockAdjustedDomainEvent(
        Guid productId,
        int previousQuantity,
        int newQuantity,
        string? reason = null)
    {
        ProductId = productId;
        PreviousQuantity = previousQuantity;
        NewQuantity = newQuantity;
        Reason = reason;
    }
}

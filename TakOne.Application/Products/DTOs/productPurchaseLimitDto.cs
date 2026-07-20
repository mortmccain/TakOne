namespace TakOne.Application.Products.DTOs;

/// <summary>
/// Read-side DTO for a single per-group purchase limit on a Product.
///
/// Mirrors the domain <c>CustomerGroupPurchaseLimit</c> value object, but
/// lives in the Application layer so the API doesn't have to reference the
/// Domain. Equality is by value (GroupName + Limit), matching the source.
/// </summary>
public sealed class ProductPurchaseLimitDto
{
    public string GroupName { get; init; } = string.Empty;
    public int Limit { get; init; }
}
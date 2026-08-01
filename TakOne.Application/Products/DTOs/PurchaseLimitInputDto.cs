namespace TakOne.Application.Products.Commands.CreateProduct;

/// <summary>
/// Input DTO for a single per-group purchase limit entry, passed as part
/// of <see cref="CreateProductCommand"/> (and other product commands that
/// accept limits).
///
/// This is an APPLICATION-layer DTO (lives in the command's namespace, not
/// in the domain), so the command layer can stay decoupled from the
/// <c>CustomerGroupPurchaseLimit</c> domain value object. The handler is
/// responsible for converting each entry into a domain value object via
/// <c>Product.SetPurchaseLimit(groupName, limit)</c>.
///
/// Validation rules (mirrored from <c>CustomerGroupPurchaseLimit.Create</c>):
///   - GroupName: 1..100 chars, non-whitespace
///   - Limit:     &gt;= 1 (a limit of 0 would mean "can never buy", which
///                isn't a meaningful configuration — use product deactivation
///                instead)
/// </summary>
public sealed class PurchaseLimitInputDto
{
    /// <summary>
    /// The customer group name this limit applies to. Must match a
    /// <c>User.GroupName</c> value used at customer-creation time, OR be a
    /// new group name (forward-looking — limits can be set for groups
    /// before any users exist in them).
    /// </summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>
    /// Maximum units of this product a single user in the group may have
    /// in their active cart at once. Must be &gt;= 1.
    /// </summary>
    public int Limit { get; init; }
}
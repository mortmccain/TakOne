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
/// <c>Product.SetPurchaseLimit(groupId, limit)</c>.
///
/// Validation rules (mirrored from <c>CustomerGroupPurchaseLimit.Create</c>):
///   - GroupId: must be a non-empty Guid (must reference an existing
///     CustomerGroup — verified by the handler via
///     ICustomerGroupRepository.GetByIdAsync).
///   - Limit:  &gt;= 1 (a limit of 0 would mean "can never buy", which
///             isn't a meaningful configuration — use product deactivation
///             instead)
/// </summary>
public sealed class PurchaseLimitInputDto
{
    /// <summary>
    /// The customer group Id this limit applies to. Must reference an
    /// existing <c>CustomerGroup</c> row — the handler validates this
    /// before calling <c>Product.SetPurchaseLimit</c>.
    /// </summary>
    public Guid GroupId { get; init; }

    /// <summary>
    /// Maximum units of this product a single user in the group may have
    /// in their active cart at once. Must be &gt;= 1.
    /// </summary>
    public int Limit { get; init; }
}
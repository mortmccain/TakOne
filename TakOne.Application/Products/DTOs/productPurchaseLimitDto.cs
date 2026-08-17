namespace TakOne.Application.Products.DTOs;

/// <summary>
/// Read-side DTO for a single per-group purchase limit on a Product.
///
/// Mirrors the domain <c>CustomerGroupPurchaseLimit</c> value object, but
/// lives in the Application layer so the API doesn't have to reference the
/// Domain. Equality is by value (GroupId + Limit), matching the source.
///
/// GROUP FIELD SEMANTICS (Salary feature, Step 3):
///   <list type="bullet">
///     <item><c>GroupId</c> — the FK to <c>CustomerGroups.Id</c>. Always
///         populated. Use this for programmatic lookups.</item>
///     <item><c>GroupName</c> — the DISPLAY name of the group, populated
///         via a batched lookup of CustomerGroup names by Id. May be null
///         if the group was hard-deleted (defensive).</item>
///   </list>
///
///   Customers never see the word "group" in any UI string. This DTO is
///   used only in admin-facing product-detail views.
/// </summary>
public sealed class ProductPurchaseLimitDto
{
    /// <summary>
    /// The customer group Id this limit applies to. Matches
    /// <c>CustomerGroupPurchaseLimit.GroupId</c> on the domain value
    /// object (changed from string GroupName in Step 1 of the salary
    /// feature).
    /// </summary>
    public Guid GroupId { get; init; }

    /// <summary>
    /// The display name of the group (looked up from CustomerGroups.Name).
    /// May be null if the group was hard-deleted — defensive only, since
    /// CustomerGroup uses soft-delete via IsActive=false.
    /// </summary>
    public string? GroupName { get; init; }

    public int Limit { get; init; }
}
using TakOne.Application.Customers.Commands.BulkApplyDefaultsForGroup;

namespace TakOne.Application.Customers.DTOs;

/// <summary>
/// Result of <see cref="BulkApplyDefaultsForGroupCommand"/>.
///
/// Returned to the UI so the success toast can show a meaningful
/// breakdown: "Applied default limit (1) to N of M products. K products
/// already had limits and were skipped."
///
/// FIELDS:
///   <see cref="TotalProductsScanned"/> — every product in the catalog
///       at the time the command ran (snapshot from GetAllProductIdsAsync).
///   <see cref="ProductsUpdated"/> — products that had NO limit row for
///       the group and received the default limit (1).
///   <see cref="ProductsSkipped"/> — products that ALREADY had a limit
///       row for the group (admin-set or pre-existing default). These
///       were left untouched to preserve admin overrides.
///
/// INVARIANT: TotalProductsScanned == ProductsUpdated + ProductsSkipped.
/// </summary>
public sealed class BulkApplyDefaultsResult
{
    public int TotalProductsScanned { get; init; }

    public int ProductsUpdated { get; init; }

    public int ProductsSkipped { get; init; }

    /// <summary>
    /// The default limit value that was applied (mirrors
    /// <c>CustomerGroupPurchaseLimit.DefaultLimit</c>). Surfaced on the
    /// result so the UI doesn't need a separate constant lookup — it
    /// just shows "Applied default limit {AppliedLimit} to ...".
    /// </summary>
    public int AppliedLimit { get; init; }
}
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Products.Commands.SetProductStock;

/// <summary>
/// Sets a Product's stock to an EXACT value (not additive — replaces the
/// current stock with the given quantity). Used by the staff "Set stock"
/// UI on ProductDetail.razor.
///
/// AUTHORIZATION:
///   Employee, Manager, Admin (same as IncreaseProductStockCommand —
///   stock changes are staff-only).
///
/// BUSINESS RULE (per user spec):
///   quantity must be ≥ 1. Setting to 0 or negative is NOT allowed via
///   this command. To make stock zero, the caller must dispatch
///   <see cref="TakOne.Application.Products.Commands.DeactivateProduct.DeactivateProductCommand"/>
///   instead (which uses the deactivation flow + popup warning). The
///   domain method <c>Product.AdjustStockTo</c> enforces this invariant
///   again as defense-in-depth.
///
/// DIFFERENCE FROM IncreaseProductStockCommand:
///   - Increase: StockQuantity += quantity (additive)
///   - Set:      StockQuantity  = quantity (absolute replacement)
/// The "Set" command is for the case where staff know the actual physical
/// count and want to sync the system to it (e.g. after a stock-take),
/// rather than calculating "current + delta" mentally.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record SetProductStockCommand(
    Guid ProductId,
    int Quantity);

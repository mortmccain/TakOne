using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Products.Commands.DeactivateProduct;

/// <summary>
/// Deactivates a Product by setting its <c>StockQuantity</c> to 0.
///
/// AUTHORIZATION:
///   Employee, Manager, Admin.
///
/// WHY SETTING STOCK TO 0 = DEACTIVATION:
///   The Product aggregate does NOT have a dedicated <c>IsActive</c> flag.
///   Per the business rule confirmed by the product owner, a "deactivated"
///   product is one the shop should stop showing to customers. The shop
///   already hides products with <c>StockQuantity == 0</c> (via the
///   <c>GetProductsPaginatedQuery</c>'s <c>IncludeInactive=false</c> path
///   which filters out zero-stock items). So setting the stock to 0 IS the
///   deactivation operation — there's no separate flag to set.
///
/// WHAT THIS DOES NOT DO:
///   - Does NOT delete the product row. The product stays in the database
///     with its original <c>Id</c>, <c>Name</c>, <c>CategoryId</c>, etc.
///     so historical sales that reference it still resolve. The product
///     just stops appearing in the shop and can't be added to new carts.
///   - Does NOT remove the product from existing carts. A draft cart with
///     this product will still show it; the next attempt to submit that
///     cart will fail on the stock check in the Sale aggregate. That's
///     the intended behavior — the customer sees their stale cart and
///     can remove the line manually.
///   - Does NOT preserve the previous stock value. The previous quantity
///     is logged via <c>ILogger</c> for audit, but the system does NOT
///     remember it. The admin UI warns the user BEFORE dispatching this
///     command: "deactivating means the stock becomes 0; if you still
///     have physical inventory, write down the current stock number so
///     you can restore it later via Restock." The user must click
///     "Confirm" in a popup before the command is dispatched.
///
/// IDEMPOTENCY:
///   Deactivating an already-zero-stock product is a no-op. The handler
///   still logs the call (audit trail), but does not throw.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record DeactivateProductCommand(Guid ProductId);

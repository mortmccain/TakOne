using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.QuickReorderLastSale;

/// <summary>
/// "Quick reorder" command — re-adds the line items from the caller's most
/// recently SUBMITTED sale to their current Draft cart (creating a fresh
/// draft if none exists).
///
/// USED BY:
///   The "سفارش سریع" (Quick Reorder) stat card on the shop page. The card
///   promises "loads their last order in the cart ready to be submitted by
///   them" — this command implements exactly that, with one important safety
///   clamp.
///
/// QUANTITY CLAMPING (per line):
///   For each line on the last submitted sale, the re-added quantity is
///   <c>min(originalQuantity, currentStock, remainingLimit)</c> where:
///     - <c>currentStock</c> is the live Product.StockQuantity at the time
///       of the reorder (the original order may have been the last units
///       of a now-out-of-stock product).
///     - <c>remainingLimit</c> = <c>currentPurchaseLimit - existingDraftQty</c>
///       for the current buyer's group on this product. This is the
///       critical safety clamp the user explicitly asked for: if the limit
///       was tightened between the original order and now, the reorder
///       must respect the new limit, not the old one. Example: original
///       order had 5 units; current limit is 2; user has 0 in the draft
///       already → reorder adds 2 (NOT 5).
///   Lines whose clamped quantity is &lt; 1 are silently skipped (the
///   product is out of stock, the limit is already exhausted in the draft,
///   or the limit was set to 0 — none are reorderable).
///
/// RETURN VALUE:
///   <c>Result&lt;Guid&gt;</c> — the Sale Id of the (possibly new) Draft.
///   The UI uses this to navigate to /Cart after a successful reorder.
///
/// EDGE CASES:
///   - User has never submitted an order → Result.Failure with a friendly
///     message ("No previous order to repeat").
///   - User has a previous order but ALL of its lines are now un-reorderable
///     (everything out of stock or limit-exhausted) → Result.Success with
///     the Sale Id, but the draft is unchanged. The UI shows a toast like
///     "0 of N items from your last order were available; cart unchanged".
///     We return Success (not Failure) because nothing went wrong — the
///     system did exactly what it was supposed to.
///
/// AUTHORIZATION:
///   All authenticated users can call this — same as CreateOrAppendSale.
///   The handler resolves the caller from ICurrentUserService.
/// </summary>
[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record QuickReorderLastSaleCommand;
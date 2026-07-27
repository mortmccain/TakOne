using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.CreateOrAppendSale;

/// <summary>
/// "Add to cart" command — the single entry point the product-detail page
/// uses when a customer taps <c>Add to Cart</c>.
///
/// DIFFERENCE vs <see cref="TakOne.Application.Sales.Commands.AddItemToSale.AddItemToSaleCommand"/>:
///   <c>AddItemToSaleCommand</c> requires a <c>SaleId</c> (an existing draft
///   already created via <c>CreateSaleCommand</c>). The product-detail page
///   doesn't know whether the customer already has a draft — they may be
///   adding their very first item. <c>CreateOrAppendSaleCommand</c> handles
///   both cases atomically:
///     - if the customer has an active Draft sale → append the line
///     - if not → create a fresh Draft sale with this line
///
/// DIFFERENCE vs <see cref="TakOne.Application.Sales.Commands.CreateSale.CreateSaleCommand"/>:
///   <c>CreateSaleCommand</c> takes a <c>CustomerWorkerId</c> + a list of
///   line items, supporting the "staff creates a sale on behalf of a
///   customer" flow. <c>CreateOrAppendSaleCommand</c> is the SELF-BUY flow:
///   the current user IS the customer, and we add one product at a time.
///
/// RETURN VALUE:
///   <c>Result&lt;Guid&gt;</c> — the Sale Id. The UI uses this to navigate
///   to <c>/Cart</c> (Phase 4.1) after a successful add, or to display
///   "Item added to cart #SALE-2026-XXXX" if the cart sidebar is later
///   added.
///
/// AUTHORIZATION:
///   All authenticated users can call this — staff buying as themselves
///   (e.g. an Employee buying for company use) is a valid use case. The
///   current user is always the customer in this command.
///
/// PURCHASE LIMIT:
///   Looked up using the CURRENT USER's <c>GroupName</c> (which is null
///   for staff). If null, no limit is enforced by the Sale aggregate —
///   staff effectively have no per-product purchase cap.
/// </summary>
[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record CreateOrAppendSaleCommand
    (
    Guid ProductId,
    int Quantity
    );
using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Sales.DTOs;

/// <summary>
/// Read-side DTO for the current user's active shopping cart.
///
/// A "cart" in TakOne is just a Sale in <c>Draft</c> status — the Sale
/// aggregate's lifecycle (Draft → Pending → Approved → Invoiced) starts in
/// the cart state. This DTO is the shape returned by
/// <see cref="TakOne.Application.Sales.Queries.GetActiveCartForUser.GetActiveCartForUserQuery"/>
/// for the <c>/Cart</c> page.
///
/// WHY A SEPARATE DTO (vs. reusing <see cref="SaleDto"/>):
///   The cart page needs ONE piece of information that <see cref="SaleDto"/>
///   does not carry: the CURRENT live stock of each product on the cart
///   (so the UI can warn "only 2 left in stock" and disable submit when a
///   line's quantity exceeds current stock). <see cref="SaleLineItemDto"/>
///   stores a price snapshot but not stock — and for a good reason: stock
///   changes constantly, and historical sales shouldn't see yesterday's
///   stock. The cart, however, is a live editable surface, so we DO want
///   live stock for each line. Hence a dedicated DTO rather than polluting
///   <c>SaleLineItemDto</c> with a field that only makes sense in the cart
///   context.
///
/// NULL CART SEMANTICS:
///   If the user has no active draft (they haven't added anything yet), the
///   query returns <c>Result&lt;CartDto?&gt;.Success(null)</c> — NOT a failure.
///   The "no cart" state is normal and the UI renders an empty-state panel
///   for it. This is different from "the load failed" (which is Failure).
/// </summary>
public sealed class CartDto
{
    /// <summary>
    /// The Sale Id of the draft. Needed by the /Cart page to dispatch
    /// update/remove/submit/clear commands (which all take a SaleId).
    /// </summary>
    public Guid SaleId { get; init; }

    /// <summary>
    /// Human-friendly sale number (e.g. "1403-00001"). Shown in the page
    /// header for context — useful if the user wants to reference their
    /// cart number with a sales employee.
    /// </summary>
    public string SaleNumber { get; init; } = string.Empty;

    /// <summary>
    /// Sum of all line item quantities. Convenience for the page header
    /// badge ("Cart (3 items)"). Computed by the handler, not stored.
    /// </summary>
    public int TotalItemCount { get; init; }

    /// <summary>
    /// The cart's current grand total. Same value as Sale.Total — surfaced
    /// here as <see cref="MoneyDto"/> so the UI doesn't need to know about
    /// the domain <c>Money</c> value object.
    /// </summary>
    public MoneyDto Total { get; init; } = new();

    /// <summary>
    /// The cart's line items, ordered by LineNumber for stable UI rendering.
    /// Never null — an empty cart (no draft) is represented by the DTO itself
    /// being null, not by an empty list here.
    /// </summary>
    public List<CartLineItemDto> LineItems { get; init; } = new();
}
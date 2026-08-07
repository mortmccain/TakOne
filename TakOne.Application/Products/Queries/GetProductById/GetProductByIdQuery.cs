using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.DTOs;
using TakOne.SharedKernel.Common;
using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Products.Queries.GetProductById;

/// <summary>
/// Loads a single Product (with its purchase limits) by Id and projects it
/// to <see cref="ProductDto"/>.
///
/// VISIBILITY RULES:
///   - All authenticated users can view any active product.
///   - Inactive products (soft-deleted / deactivated) are visible only to
///     Admin / Manager / Employee. Customers should not see them in the shop.
///     (For this query, we return the product regardless of state; the shop
///     list query applies the active filter. The detail page can show a
///     "this product is no longer available" notice if needed.)
///
/// PURCHASE LIMITS:
///   All purchase limits for the product are returned. Customers SHOULD see
///   their own limit (filtered client-side or via a separate "my limit"
///   endpoint), but should NEVER see limits for OTHER groups. Admins and
///   managers can see all limits. The handler respects this by checking
///   <c>currentUser.IsInRole(Roles.Admin|Roles.Manager)</c> — if not admin
///   or manager, the purchase-limits collection is cleared in the DTO.
///
///   This is a defense-in-depth measure: the customer's UI is supposed to
///   only render their own limit anyway, but the API never sends the other
///   groups' limits over the wire.
/// </summary>
[RequireAuthentication]
public sealed class GetProductByIdQuery
{
    public Guid ProductId { get; init; }
}
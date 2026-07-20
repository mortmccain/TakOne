using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.ApproveSale;

/// <summary>
/// Transitions a Sale from Pending to Approved. After approval, the sale
/// can be marked as invoiced or cancelled.
///
/// AUTHORIZATION:
///   Employee, Manager, Admin. Customers cannot approve their own sales
///   (conflict of interest). ReadOnly cannot approve anything.
///
/// STOCK SIDE-EFFECT:
///   This is the moment when Product stock is decremented for each line item.
///   The Sale aggregate itself does NOT touch Product stock (it doesn't load
///   the Product aggregate); the handler does it in the same EF Core
///   transaction as the status transition, so the decrement and the status
///   change commit atomically.
///
/// CONCURRENCY:
///   If two staff members approve two different sales that both include the
///   same product, and the combined quantity exceeds stock, the second
///   approval's SaveChangesAsync will fail. We rely on EF Core's optimistic
///   concurrency token on Product.StockQuantity (configured in Infrastructure)
///   to detect this; the resulting DbUpdateConcurrencyException surfaces to
///   the caller as a 409-style error.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record ApproveSaleCommand(Guid SaleId);
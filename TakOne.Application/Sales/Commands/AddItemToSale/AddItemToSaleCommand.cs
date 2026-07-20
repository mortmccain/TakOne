using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.AddItemToSale;

/// <summary>
/// Adds a line item to a Draft Sale. If a line for the same Product already
/// exists on the sale, the aggregate increments that line's quantity by
/// <see cref="Quantity"/> (rather than creating a second line).
///
/// AUTHORIZATION:
///   Everyone authenticated can call this. Role check via
///   <see cref="RequireRolesAttribute"/> + AuthorizationMiddleware.
///
/// OWNERSHIP:
///   The handler enforces that <c>Sale.CreatedByUserId == currentUser.UserId</c>.
///   For a self-buy, that's the customer. For an on-behalf sale, that's the
///   staff member who started the draft — they own it until they submit it.
///   The customer (Sale.CustomerId) cannot edit a draft that someone else is
///   building for them.
///
/// STATUS:
///   Only Draft sales accept new items. Pending/Approved/Invoiced/Cancelled
///   sales are immutable on the line-item side.
/// </summary>
[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record AddItemToSaleCommand(
    Guid SaleId,
    Guid ProductId,
    int Quantity);
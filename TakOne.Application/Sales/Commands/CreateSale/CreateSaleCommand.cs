using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.CreateSale;

/// <summary>
/// Creates a new Sale in Draft status, with line items, in a single command.
///
/// AUTHORIZATION:
///   Everyone authenticated can create a sale — customers buy for themselves,
///   staff (Employee/Manager/Admin) may also buy on behalf of a customer via
///   the dedicated sales-employee page. Role check is enforced by
///   <see cref="AuthorizationMiddleware"/> via <see cref="RequireRolesAttribute"/>.
///
/// CUSTOMER vs CREATOR:
///   <see cref="CustomerWorkerId"/> is the personal worker ID of the customer
///   the sale is FOR. The handler resolves this to a User and stores:
///     - Sale.CustomerId       = customer.Id
///     - Sale.CustomerName     = customer.FullName  (snapshot)
///     - Sale.CreatedByUserId  = current user's Id  (the authenticated user)
///     - Sale.CreatedByName    = current user's FullName (snapshot)
///
///   If the creator IS the customer (self-buy), then CustomerId == CreatedByUserId.
///   If a staff member is creating the sale on behalf of a customer, they differ.
///   The aggregate doesn't care — it just stores both. The submitter-must-be-creator
///   rule is enforced in <see cref="SubmitSaleCommandHandler"/>.
///
/// LINE ITEMS:
///   Provided inline so the customer can build their cart in one shot. Each item
///   is just ProductId + Quantity — the handler loads each product (for name,
///   price snapshot, stock check, and group purchase-limit lookup).
///
/// PURCHASE LIMITS:
///   Looked up per-line using the CUSTOMER's GroupName (NOT the creator's),
///   because in an on-behalf purchase the customer is the one whose group
///   limit applies. Staff have no GroupName — using theirs would bypass the limit.
/// </summary>
[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record CreateSaleCommand(
    string CustomerWorkerId,
    IReadOnlyList<CreateSaleItem> Items);

/// <summary>
/// A single line in a <see cref="CreateSaleCommand"/>.
/// Quantity must be ≥ 1 (enforced by validator).
/// </summary>
public sealed record CreateSaleItem(Guid ProductId, int Quantity);
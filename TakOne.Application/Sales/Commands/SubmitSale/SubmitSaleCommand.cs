using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.SubmitSale;

/// <summary>
/// Transitions a Sale from Draft to Pending. This is the customer's "submit
/// cart" action — after submission, the sale can no longer be modified; it
/// awaits staff approval.
///
/// BUSINESS RULE (the one forbidden thing):
///   A sale MUST be submitted by its own creator. A customer cannot create a
///   draft and have an employee submit it on their behalf. This is enforced
///   in the handler by comparing Sale.CreatedByUserId to currentUser.UserId.
///
///   Note: an employee CAN create a sale on behalf of a customer (in which
///   case the employee is the creator) and submit it themselves — that's
///   allowed. What's NOT allowed is splitting create-and-submit across two
///   different people.
/// </summary>
[RequireRoles(Roles.Customer, Roles.Employee, Roles.Manager, Roles.Admin)]
public sealed record SubmitSaleCommand(Guid SaleId);
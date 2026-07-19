using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Sales.Commands.ApproveSale;

/// <summary>
/// Approves a Pending Sale. Only staff with the Employee or Manager role
/// can approve — enforced by <see cref="RequireRolesAttribute"/>.
///
/// The approver's user Id is captured in sale.ApprovedByUserId for auditing.
/// </summary>
[RequireRoles(Roles.Employee, Roles.Manager)]
public sealed record ApproveSaleCommand(Guid SaleId);
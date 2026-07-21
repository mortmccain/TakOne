using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Users.Commands.CreateStaff;

/// <summary>
/// Creates a new STAFF user (Employee, Manager, ReadOnly, or Admin).
///
/// AUTHORIZATION:
///   Admin only. Managers cannot create staff — only admins can. This is
///   stricter than CreateCustomer (which managers can do) because staff
///   accounts have privileged access.
///
/// ROLE PARAMETER:
///   The caller specifies which staff role to assign (one of
///   <see cref="Roles.Employee"/>, <see cref="Roles.Manager"/>,
///   <see cref="Roles.ReadOnly"/>, <see cref="Roles.Admin"/>).
///   The handler validates that the role is a known staff role before
///   delegating to <see cref="Common.Interfaces.IUserAccountService"/>.
///
/// TWO-PHASE CREATION:
///   Same as <see cref="CreateCustomerCommand"/> — Domain User first,
///   then ApplicationUser with shared Guid Id, then SaveChangesAsync.
///
/// NO GROUP:
///   Staff users do NOT belong to a customer group. Their GroupName is
///   always null. Per-product purchase limits do not apply to staff.
///   (Staff can still buy on their own behalf — when they do, no purchase
///   limit is enforced, because the lookup returns null.)
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed record CreateStaffCommand
    (
    string WorkerId,
    string FullName,
    string Email,
    string InitialPassword,
    string Role
    );
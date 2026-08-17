using TakOne.Application.Common.Authorization;
using TakOne.Domain.Users;

namespace TakOne.Application.Users.Commands.CreateCustomer;

/// <summary>
/// Creates a new CUSTOMER user.
///
/// AUTHORIZATION:
///   Manager, Admin. Employees cannot create users — even customers —
///   because creating a customer means assigning them to a group, and
///   group membership determines purchase limits + salary budget (a
///   financial concern).
///
/// TWO-PHASE CREATION:
///   1. The handler creates the Domain <c>User</c> via
///      <see cref="Domain.Users.User.CreateCustomer"/> and persists it
///      via <see cref="Common.Interfaces.IUserRepository.AddAsync"/>.
///      This generates the User's Guid Id.
///   2. The handler then calls
///      <see cref="Common.Interfaces.IUserAccountService.CreateIdentityAccountAsync"/>
///      with that SAME Guid Id to create the ApplicationUser (ASP.NET
///      Identity), set its email + password, and assign the Customer role.
///   Both phases must succeed. If phase 2 fails, the handler returns the
///   failure — the Domain User is still in the DbContext's change tracker
///   but SaveChangesAsync has not been called, so nothing persists.
///
/// UNIQUENESS:
///   WorkerId must be unique across all users (it's the login identifier).
///   Enforced by the handler via
///   <see cref="Common.Interfaces.IUserRepository.WorkerIdExistsAsync"/>,
///   and backed by a unique index on ApplicationUser.UserName in the DB.
///
/// GROUP VISIBILITY:
///   The customer's GroupId is required at creation (per domain invariant).
///   The group's NAME is NEVER shown to the customer themselves — only to
///   admins and managers. See <see cref="Domain.Users.User"/> XML docs.
///
/// SALARY FEATURE (Step 3):
///   Takes <c>GroupId</c> (Guid) instead of <c>GroupName</c> (string).
///   The UI must select from existing groups via the
///   <c>GetAllCustomerGroupsQuery</c>.
///
/// GENDER (Phase 0.5):
///   Required field. Per roadmap Section 12.5, only Male and Female are
///   valid values. The Domain factory defaults to Male if the caller
///   doesn't care, but at the command boundary we require an explicit
///   value so the create-user form always submits one.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record CreateCustomerCommand
    (
    string WorkerId,
    string FullName,
    Guid GroupId,
    string Email,
    string InitialPassword,
    Gender Gender
    );
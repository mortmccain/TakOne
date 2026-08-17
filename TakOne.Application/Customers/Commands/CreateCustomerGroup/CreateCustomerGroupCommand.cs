using TakOne.Application.Common.Authorization;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Application.Customers.Commands.CreateCustomerGroup;

/// <summary>
/// Creates a new customer group (CustomerGroup aggregate).
///
/// AUTHORIZATION:
///   Manager, Admin. Employees cannot create groups — group membership
///   determines salary budget + purchase limits (a financial concern).
///
/// SEMANTICS:
///   - Name must be unique (enforced by the handler via
///     <c>ICustomerGroupRepository.NameExistsAsync</c>, backed by a
///     DB unique index).
///   - Salary is a Money value object (amount + ISO currency). The
///     currency MUST match the currency of products that customers in
///     this group will buy (currency matching always applies — see
///     <c>IPurchaseLimitPolicy.IsCurrencyMatchAsync</c>).
///   - New groups are created with <c>IsActive = true</c>. Use
///     <c>DeactivateCustomerGroupCommand</c> to soft-delete later.
///
/// POST-CONDITION:
///   After this command succeeds, the new group's Id can be used in:
///     - <c>AssignUserToGroupCommand</c> (assign customers to the group)
///     - <c>SetProductPurchaseLimitCommand</c> (set per-product count limits
///       for the group)
///     - The CreateProduct page's per-group limit editor
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record CreateCustomerGroupCommand(
    string Name,
    decimal SalaryAmount,
    string SalaryCurrency);
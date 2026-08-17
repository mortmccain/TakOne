using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Customers.Commands.UpdateCustomerGroupSalary;

/// <summary>
/// Updates the monthly salary for an existing customer group.
///
/// AUTHORIZATION:
///   Manager, Admin.
///
/// SEMANTICS:
///   - The new salary takes effect IMMEDIATELY for new cart mutations.
///   - The new salary does NOT retroactively change the consumed amount
///     for the current month — if the customer already spent 5,000,000 IRR
///     under a 10,000,000 IRR salary, and the admin lowers the salary to
///     3,000,000 IRR, the customer's remaining budget becomes NEGATIVE
///     (3,000,000 − 5,000,000 = −2,000,000). The customer cannot add
///     ANYTHING to their cart until next month.
///   - The salary's CURRENCY cannot be changed via this command —
///     changing currency would invalidate already-purchased products
///     priced in the old currency. To change currency, deactivate this
///     group and create a new one.
/// </summary>
[RequireRoles(Roles.Manager, Roles.Admin)]
public sealed record UpdateCustomerGroupSalaryCommand(
    Guid GroupId,
    decimal NewSalaryAmount);

using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Customers.DTOs;

/// <summary>
/// Read-side DTO for a single customer group, used by the ManageGroups page.
///
/// Exposes the full <c>CustomerGroup</c> aggregate state for admin/manager
/// display + edit forms.
/// </summary>
public sealed class CustomerGroupDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The group's monthly salary (Money: amount + ISO currency).
    /// Used to compute the customer's monthly budget — see
    /// <c>ISalaryBudgetService</c>.
    /// </summary>
    public MoneyDto Salary { get; init; } = new();

    public bool IsActive { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }

    /// <summary>
    /// The count of active users currently assigned to this group.
    /// Loaded via <c>ICustomerGroupRepository.GetActiveUserCountAsync</c>
    /// — used by the delete/deactivate flow to warn the admin how many
    /// users will be affected.
    /// </summary>
    public int ActiveUserCount { get; init; }
}
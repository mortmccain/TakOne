using TakOne.SharedKernel.DTOs;

namespace TakOne.Application.Customers.DTOs;

/// <summary>
/// Lightweight DTO for the customer-group list (ManageGroups page) and
/// the group dropdowns on CreateUser / CreateProduct / SetProductPurchaseLimit.
///
/// Excludes the audit timestamps (CreatedAtUtc / UpdatedAtUtc) — those are
/// only shown on the EditGroup page (which uses the full
/// <see cref="CustomerGroupDto"/>).
/// </summary>
public sealed class CustomerGroupListItemDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The group's monthly salary (Money: amount + ISO currency).
    /// Shown in the dropdown so the admin can quickly scan group budgets.
    /// </summary>
    public MoneyDto Salary { get; init; } = new();

    public bool IsActive { get; init; }
}
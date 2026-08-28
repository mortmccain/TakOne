using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Customers.Events;

/// <summary>
/// Raised when a <see cref="Entities.CustomerGroup"/>'s monthly salary
/// is updated via <see cref="Entities.CustomerGroup.UpdateSalary"/>.
/// Carries both the previous and new salary so audit subscribers can
/// reconstruct the change history.
/// </summary>
public sealed class CustomerGroupSalaryUpdatedDomainEvent : BaseDomainEvent
{
    public Guid CustomerGroupId { get; }
    public Money PreviousSalary { get; }
    public Money NewSalary { get; }

    public CustomerGroupSalaryUpdatedDomainEvent(
        Guid customerGroupId,
        Money previousSalary,
        Money newSalary)
    {
        CustomerGroupId = customerGroupId;
        PreviousSalary = previousSalary;
        NewSalary = newSalary;
    }
}

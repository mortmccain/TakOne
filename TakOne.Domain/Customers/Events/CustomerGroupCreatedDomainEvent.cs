using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Customers.Events;

/// <summary>
/// Raised when a new <see cref="Entities.CustomerGroup"/> is created
/// via the <see cref="Entities.CustomerGroup.Create"/> factory.
/// </summary>
public sealed class CustomerGroupCreatedDomainEvent : BaseDomainEvent
{
    public Guid CustomerGroupId { get; }
    public string Name { get; }
    public Money Salary { get; }

    public CustomerGroupCreatedDomainEvent(Guid customerGroupId, string name, Money salary)
    {
        CustomerGroupId = customerGroupId;
        Name = name;
        Salary = salary;
    }
}

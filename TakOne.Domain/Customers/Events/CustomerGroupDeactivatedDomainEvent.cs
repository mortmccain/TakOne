using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Customers.Events;

/// <summary>
/// Raised when a <see cref="Entities.CustomerGroup"/> is deactivated
/// via <see cref="Entities.CustomerGroup.Deactivate"/>.
/// </summary>
public sealed class CustomerGroupDeactivatedDomainEvent : BaseDomainEvent
{
    public Guid CustomerGroupId { get; }

    public CustomerGroupDeactivatedDomainEvent(Guid customerGroupId)
    {
        CustomerGroupId = customerGroupId;
    }
}

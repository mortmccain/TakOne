using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Customers.Events;

/// <summary>
/// Raised when a previously-deactivated <see cref="Entities.CustomerGroup"/>
/// is reactivated via <see cref="Entities.CustomerGroup.Activate"/>.
/// </summary>
public sealed class CustomerGroupActivatedDomainEvent : BaseDomainEvent
{
    public Guid CustomerGroupId { get; }

    public CustomerGroupActivatedDomainEvent(Guid customerGroupId)
    {
        CustomerGroupId = customerGroupId;
    }
}

using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Users.Events;

/// <summary>
/// Raised when a previously-deactivated <see cref="Entities.User"/>
/// is reactivated via <see cref="Entities.User.Activate"/>.
/// </summary>
public sealed class UserActivatedDomainEvent : BaseDomainEvent
{
    public Guid UserId { get; }

    public UserActivatedDomainEvent(Guid userId)
    {
        UserId = userId;
    }
}

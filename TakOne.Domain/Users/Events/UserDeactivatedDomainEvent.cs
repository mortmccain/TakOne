using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Users.Events;

/// <summary>
/// Raised when a <see cref="Entities.User"/> is deactivated via
/// <see cref="Entities.User.Deactivate"/>. Subscribers can use this
/// to invalidate active-session caches, terminate SignalR connections,
/// revoke refresh tokens, etc.
/// </summary>
public sealed class UserDeactivatedDomainEvent : BaseDomainEvent
{
    public Guid UserId { get; }

    public UserDeactivatedDomainEvent(Guid userId)
    {
        UserId = userId;
    }
}

using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Users.Events;

/// <summary>
/// Raised when a <see cref="Entities.User"/> is removed from their
/// customer group via <see cref="Entities.User.RemoveFromGroup"/>.
/// Carries the previous GroupId so audit subscribers can reconstruct
/// the removal history.
/// </summary>
public sealed class UserRemovedFromGroupDomainEvent : BaseDomainEvent
{
    public Guid UserId { get; }
    public Guid? PreviousGroupId { get; }

    public UserRemovedFromGroupDomainEvent(Guid userId, Guid? previousGroupId)
    {
        UserId = userId;
        PreviousGroupId = previousGroupId;
    }
}

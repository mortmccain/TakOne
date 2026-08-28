using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Users.Events;

/// <summary>
/// Raised when a <see cref="Entities.User"/> is assigned to a customer
/// group via <see cref="Entities.User.AssignToGroup"/>. Carries both
/// the previous and new GroupId so audit subscribers can reconstruct
/// the assignment history without re-querying.
/// </summary>
public sealed class UserAssignedToGroupDomainEvent : BaseDomainEvent
{
    public Guid UserId { get; }
    public Guid? PreviousGroupId { get; }
    public Guid NewGroupId { get; }

    public UserAssignedToGroupDomainEvent(Guid userId, Guid? previousGroupId, Guid newGroupId)
    {
        UserId = userId;
        PreviousGroupId = previousGroupId;
        NewGroupId = newGroupId;
    }
}

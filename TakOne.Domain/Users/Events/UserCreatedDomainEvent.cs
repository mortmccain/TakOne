using TakOne.SharedKernel.Common;

namespace TakOne.Domain.Users.Events;

/// <summary>
/// Raised when a new <see cref="Entities.User"/> is created via the
/// <see cref="Entities.User.CreateCustomer"/> or
/// <see cref="Entities.User.CreateStaff"/> factory. Carries the
/// minimal state needed by subscribers (audit log, search index, cache
/// invalidation).
/// </summary>
public sealed class UserCreatedDomainEvent : BaseDomainEvent
{
    public Guid UserId { get; }
    public string WorkerId { get; }
    public string FullName { get; }
    public Guid? GroupId { get; }
    public Gender Gender { get; }

    public UserCreatedDomainEvent(
        Guid userId,
        string workerId,
        string fullName,
        Guid? groupId,
        Gender gender)
    {
        UserId = userId;
        WorkerId = workerId;
        FullName = fullName;
        GroupId = groupId;
        Gender = gender;
    }
}

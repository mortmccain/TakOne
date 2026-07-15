using TakOne.SharedKernel.Common;

namespace TakOne.SharedKernel.Primitives;

/// <summary>
/// Base class for Aggregate Roots. Manages a collection of Domain Events
/// that are dispatched when the Unit of Work saves changes.
/// </summary>
public abstract class AggregateRoot : BaseEntity
{
    private readonly List<BaseDomainEvent> _domainEvents = new();

    public IReadOnlyCollection<BaseDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected AggregateRoot(Guid id) : base(id) { }
    protected AggregateRoot() : base() { }

    protected void AddDomainEvent(BaseDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }

    /// <summary>
    /// Removes the specified domain events from the aggregate's domain event list.
    /// This allows publishing code to remove only the events that were published
    /// while leaving any new events added by handlers intact.
    /// </summary>
    /// <param name="events">Events to remove.</param>
    public void RemoveDomainEvents(IEnumerable<BaseDomainEvent> events)
    {
        foreach (var e in events)
        {
            _domainEvents.Remove(e);
        }
    }
}
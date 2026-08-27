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

    /// <summary>
    /// Clears all accumulated domain events from the aggregate. Called by
    /// Wolverine's <c>PublishDomainEventsFromEntityFrameworkCore</c>
    /// domain-event scraper after it has read + dispatched the events via
    /// the enrolled outbox, OR by application code that needs to suppress
    /// event dispatch for the current unit of work.
    /// </summary>
    /// <remarks>
    /// In practice, aggregates are per-request (loaded → mutated → saved →
    /// discarded at the end of the handler's DI scope), so even if this
    /// method is not invoked, events do NOT accumulate across requests —
    /// each request loads a fresh aggregate instance with an empty
    /// <c>_domainEvents</c> list. This method is the defensive cleanup
    /// hook for the within-scope case (e.g. a handler that saves the same
    /// aggregate instance twice in one unit of work).
    /// </remarks>
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }

    /// <summary>
    /// Removes only the specified domain events from the aggregate's event
    /// list. This is the surgical alternative to <see cref="ClearDomainEvents"/>:
    /// publishing code can remove exactly the events it has dispatched
    /// while leaving any new events added by handlers intact (useful when
    /// a domain-event handler itself adds further domain events to the
    /// same aggregate during the SaveChanges pipeline).
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
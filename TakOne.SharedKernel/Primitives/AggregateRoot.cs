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

    /// <summary>
    /// Optimistic-concurrency token. EF Core maps this as a SQL Server
    /// <c>rowversion</c> column (the DB auto-increments it on every
    /// UPDATE). When two concurrent transactions load the same aggregate
    /// and both try to save, the SECOND save sees the row's RowVersion has
    /// changed and throws <c>DbUpdateConcurrencyException</c> — which the
    /// handler catches and surfaces as a friendly "the data was modified
    /// by another user, please retry" error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>WHY THIS EXISTS (Brutal Code Review v3 #14):</b> The v1 and v2
    /// reviews flagged the absence of any concurrency token as the
    /// highest-impact unfixed issue. The in-process
    /// <c>ISaleStateLock</c> (added in v2) only protects single-node
    /// deployments — it does NOT protect multi-node deployments or direct
    /// DB writes. <c>Product.DecreaseStock</c> was a classic check-then-act
    /// race: load product → check stock &gt; quantity → decrement → save.
    /// Two concurrent requests could both read stock=10, both pass the
    /// check, both decrement to 5, and the second save silently overwrites
    /// the first.
    /// </para>
    /// <para>
    /// <b>HOW IT MAPPS:</b> The <c>ApplicationDbContext.OnModelCreating</c>
    /// applies a convention: every entity type that exposes a
    /// <c>RowVersion</c> property gets <c>.IsRowVersion()</c> configured
    /// automatically. This means adding the property to
    /// <c>AggregateRoot</c> is sufficient — no per-entity configuration
    /// needed.
    /// </para>
    /// <para>
    /// <b>NULLABILITY:</b> The <c>= default!</c> initializer suppresses
    /// the nullable-warning. EF Core materializes the value from the DB
    /// on read; for new entities, EF sends the default (null/empty) and
    /// SQL Server assigns the first rowversion value on INSERT.
    /// </para>
    /// </remarks>
    public byte[] RowVersion { get; private set; } = default!;

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
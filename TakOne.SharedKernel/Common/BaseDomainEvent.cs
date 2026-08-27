using System;
using System.Collections.Generic;
using System.Text;

namespace TakOne.SharedKernel.Common;

    /// <summary>
    /// Base class for all Domain Events. Dispatched by Wolverine's
    /// <c>PublishDomainEventsFromEntityFrameworkCore&lt;AggregateRoot,
    /// BaseDomainEvent&gt;</c> extension at <c>SaveChangesAsync</c> time
    /// through the enrolled Wolverine outbox (atomic with the originating
    /// EF Core transaction).
    /// </summary>
    public abstract class BaseDomainEvent
    {
        public Guid EventId { get; } = Guid.NewGuid();
        public DateTime OccurredOn { get; } = DateTime.UtcNow;
    }
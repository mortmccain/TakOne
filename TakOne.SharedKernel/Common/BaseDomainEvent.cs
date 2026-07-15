using System;
using System.Collections.Generic;
using System.Text;

namespace TakOne.SharedKernel.Common;

    /// <summary>
    /// Base class for all Domain Events. Implements INotification for MediatR dispatch.
    /// </summary>
    public abstract class BaseDomainEvent
    {
        public Guid EventId { get; } = Guid.NewGuid();
        public DateTime OccurredOn { get; } = DateTime.UtcNow;
    }
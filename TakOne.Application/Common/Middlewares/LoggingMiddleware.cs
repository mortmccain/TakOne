using Microsoft.Extensions.Logging;
using Wolverine;

namespace TakOne.Application.Common.Middlewares;

/// <summary>
/// Logs the start and completion of every command/query execution.
/// Does NOT log the current user because ICurrentUserService (Blazor-specific)
/// cannot be resolved outside the Razor component DI scope.
/// </summary>
/// <remarks>
/// <para>
/// <b>LoggerMessage source generators (CA1848).</b> Wolverine invokes these
/// middleware callbacks for EVERY message flowing through the local queue —
/// the highest-frequency logging path in the application. The
/// <c>[LoggerMessage]</c>-decorated partial methods below compile to
/// strongly-typed <c>ILogger.Log</c> calls with a cached delegate and a
/// compile-time-built message template, eliminating the per-call
/// <c>params object?[]</c> boxing/allocations of the
/// <c>LoggerExtensions.LogInformation</c> path, and skipping argument
/// formatting entirely when the level is disabled.
/// </para>
/// <para>
/// <b>Event-noise polish (Round 2).</b> The middleware policy applies to
/// domain-event handler chains as well as command/query handlers, so a
/// single user action (e.g. adding a cart line) previously emitted
/// "Starting X / Completed X" pairs at Information level for the COMMAND
/// <i>and for every internal domain event it raised</i> (SaleLineItemAdded,
/// NotificationCreated, …) — tripling the log volume on the hottest paths
/// with machinery entries nobody asks for. Domain events (messages whose
/// type name ends with "DomainEvent" — the same convention
/// <see cref="AuthorizationMiddleware"/> uses for its exemption) now log
/// at <b>Debug</b> level, keeping the Information stream focused on
/// user-initiated commands and queries while retaining full fanout
/// traceability when Debug logging is enabled.
/// </para>
/// <para>
/// The rendered output is byte-identical to the previous extension-method
/// implementation ("Starting {RequestName}" / "Completed {RequestName}") —
/// a deliberate constraint: log-based dashboards and the
/// <c>LoggingMiddlewareTests</c> suite assert on these templates.
/// </para>
/// </remarks>
public sealed partial class LoggingMiddleware
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting {RequestName}")]
    private partial void LogStarting(string requestName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting {RequestName}")]
    private partial void LogStartingEvent(string requestName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed {RequestName}")]
    private partial void LogCompleted(string requestName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Completed {RequestName}")]
    private partial void LogCompletedEvent(string requestName);

    public Task BeforeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var messageName = envelope.Message?.GetType().Name ?? "Unknown";

        if (messageName.EndsWith("DomainEvent", StringComparison.Ordinal))
        {
            // Internal fanout machinery — Debug keeps the Information
            // stream focused on user-initiated commands/queries.
            LogStartingEvent(messageName);
        }
        else
        {
            LogStarting(messageName);
        }

        return Task.CompletedTask;
    }

    public Task AfterAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var messageName = envelope.Message?.GetType().Name ?? "Unknown";

        if (messageName.EndsWith("DomainEvent", StringComparison.Ordinal))
        {
            LogCompletedEvent(messageName);
        }
        else
        {
            LogCompleted(messageName);
        }

        return Task.CompletedTask;
    }
}

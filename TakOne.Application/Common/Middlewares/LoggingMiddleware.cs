using Microsoft.Extensions.Logging;
using Wolverine;

namespace TakOne.Application.Common.Middlewares;

/// <summary>
/// Logs the start and completion of every command/query execution.
/// Does NOT log the current user because ICurrentUserService (Blazor-specific)
/// cannot be resolved outside the Razor component DI scope.
/// </summary>
public sealed class LoggingMiddleware
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public Task BeforeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var requestName = envelope.Message?.GetType().Name ?? "Unknown";
        _logger.LogInformation("Starting {RequestName}", requestName);
        return Task.CompletedTask;
    }

    public Task AfterAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var requestName = envelope.Message?.GetType().Name ?? "Unknown";
        _logger.LogInformation("Completed {RequestName}", requestName);
        return Task.CompletedTask;
    }
}
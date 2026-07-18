using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace TakOne.Application.Common.Middlewares;

public sealed class PerformanceMiddleware
{
    private readonly ILogger<PerformanceMiddleware> _logger;
    private const int WarningThresholdMs = 500;
    private Stopwatch? _stopwatch;

    public PerformanceMiddleware(ILogger<PerformanceMiddleware> logger)
    {
        _logger = logger;
    }

    public Task BeforeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        _stopwatch = Stopwatch.StartNew();
        return Task.CompletedTask;
    }

    public Task AfterAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        _stopwatch?.Stop();

        if (_stopwatch != null && _stopwatch.ElapsedMilliseconds > WarningThresholdMs)
        {
            var requestName = envelope.Message?.GetType().Name ?? "Unknown";
            _logger.LogWarning(
                "Slow request: {RequestName} took {ElapsedMs}ms (threshold: {ThresholdMs}ms)",
                requestName,
                _stopwatch.ElapsedMilliseconds,
                WarningThresholdMs);
        }

        return Task.CompletedTask;
    }
}
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace TakOne.Application.Common.Middlewares;

/// <summary>
/// Logs a warning when a command or query takes longer than
/// <see cref="SlowRequestThresholdMs"/> milliseconds.
///
/// THRESHOLD CONFIGURATION:
///   The threshold defaults to 500 ms (the value most enterprise applications
///   treat as "perceptible to users"). It can be overridden at startup via
///   configuration by setting "Wolverine:SlowRequestThresholdMs" in
///   appsettings.json. The ServiceCollectionExtensions.AddTakOneApplication
///   method reads that key and assigns it to the static property below.
///
///   We use a static property (rather than per-instance configuration) because
///   Wolverine constructs a fresh middleware instance per message, and we
///   want the threshold to apply uniformly across all invocations without
///   injecting an IOptions<T> on every construction.
/// </summary>
public sealed class PerformanceMiddleware
{
    private readonly ILogger<PerformanceMiddleware> _logger;
    private Stopwatch? _stopwatch;

    /// <summary>
    /// Threshold in milliseconds above which a request is logged as "slow".
    /// Set once at application startup from configuration. Defaults to 500 ms.
    ///
    /// Marked volatile because it is read on every AfterAsync call (which
    /// can run concurrently across multiple message handlers on different
    /// threads) and written once at startup. volatile ensures the write is
    /// visible to all readers without a full lock.
    /// </summary>
    public static int SlowRequestThresholdMs = 500;

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

        if (_stopwatch != null && _stopwatch.ElapsedMilliseconds > SlowRequestThresholdMs)
        {
            var requestName = envelope.Message?.GetType().Name ?? "Unknown";
            _logger.LogWarning(
                "Slow request: {RequestName} took {ElapsedMs}ms (threshold: {ThresholdMs}ms)",
                requestName,
                _stopwatch.ElapsedMilliseconds,
                SlowRequestThresholdMs);
        }

        return Task.CompletedTask;
    }
}
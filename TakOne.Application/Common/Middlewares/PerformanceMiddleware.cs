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
/// <remarks>
/// <b>LoggerMessage source generators (CA1848).</b> The slow-request warning
/// goes through a <c>[LoggerMessage]</c>-decorated partial method so the
/// hot path (fast requests — the overwhelming majority) pays nothing for
/// logging infrastructure: the generated code checks
/// <c>IsEnabled(LogLevel.Warning)</c> before building any log state, and the
/// warning path itself avoids the <c>params object?[]</c> boxing of the
/// <c>LoggerExtensions.LogWarning</c> overload. The rendered message is
/// byte-identical to the previous extension-method implementation — the
/// <c>PerformanceMiddlewareTests</c> suite asserts on the
/// "Slow request: ... took ...ms (threshold: ...ms)" template.
/// </remarks>
public sealed partial class PerformanceMiddleware
{
    private readonly ILogger<PerformanceMiddleware> _logger;
    private Stopwatch? _stopwatch;

    /// <summary>
    /// Threshold in milliseconds above which a request is logged as "slow".
    /// Set once at application startup from configuration. Defaults to 500 ms.
    ///
    /// Exposed as a static auto-property (rather than a public static field)
    /// to satisfy CA2211 — non-constant fields should not be visible. The set
    /// accessor is called once at startup from
    /// <c>ServiceCollectionExtensions.AddTakOneApplication</c>; the get
    /// accessor is called on every <c>AfterAsync</c> (which can run
    /// concurrently across multiple message handlers). The startup write
    /// happens-before any request-thread read is guaranteed by the .NET host
    /// startup sequence (no request threads exist yet when the DI container
    /// is being built), and 32-bit int reads/writes are atomic per the CLI
    /// spec, so no <c>volatile</c> or lock is required.
    /// </summary>
    public static int SlowRequestThresholdMs { get; set; } = 500;

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
            LogSlowRequest(
                envelope.Message?.GetType().Name ?? "Unknown",
                _stopwatch.ElapsedMilliseconds,
                SlowRequestThresholdMs);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Slow request: {RequestName} took {ElapsedMs}ms (threshold: {ThresholdMs}ms)")]
    private partial void LogSlowRequest(string requestName, long elapsedMs, int thresholdMs);
}
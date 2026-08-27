using Microsoft.Extensions.Logging;

namespace TakOne.Application.Tests.Common.Middlewares;

/// <summary>
/// Spy implementation of <see cref="ILogger{TCategoryName}"/> that captures
/// the rendered log message. Shared by LoggingMiddlewareTests and
/// PerformanceMiddlewareTests — both tests need to assert on the FORMATTED
/// log string (NSubstitute's argument specifications can't match the
/// underlying Log method's generic TState parameter directly for the
/// formatted message).
/// </summary>
internal sealed class SpyLogger<TCategoryName> : ILogger<TCategoryName>
{
    public string? LastMessage { get; private set; }
    public LogLevel LastLogLevel { get; private set; }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
        => NullDisposable.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        LastLogLevel = logLevel;
        LastMessage = formatter(state, exception);
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}

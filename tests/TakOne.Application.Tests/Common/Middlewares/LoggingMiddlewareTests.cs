using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Middlewares;
using Wolverine;
using Xunit;

namespace TakOne.Application.Tests.Common.Middlewares;

/// <summary>
/// Unit tests for <see cref="LoggingMiddleware"/>.
///
/// COVERAGE APPROACH:
///   The middleware logs "Starting {RequestName}" in BeforeAsync and
///   "Completed {RequestName}" in AfterAsync. The request name is
///   envelope.Message?.GetType().Name ?? "Unknown". Tests cover:
///     • BeforeAsync with a non-null message — logs the type name.
///     • BeforeAsync with a null message — logs "Unknown".
///     • AfterAsync with a non-null message — logs the type name.
///     • AfterAsync with a null message — logs "Unknown".
///     • Each method calls LogInformation exactly once.
///     • The CancellationToken is accepted but not used.
///
/// NSUBSTITUTE / ILogger NOTE (carried over from Round 17-A):
///   The `LogInformation(...)` extension method on `ILogger` wraps the
///   underlying `Log<TState>` method with a different argument shape (2 args
///   vs 5 args). NSubstitute's argument specifications can't match between
///   the two. The correct assertion pattern uses the underlying Log method
///   with `NSubstitute.Arg.AnyType` to match the generic TState parameter.
/// </summary>
public class LoggingMiddlewareTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    // A representative message type — the handler's "Starting {RequestName}"
    // template fills with this type's name.
    private sealed class SampleCommand;

    // A representative DOMAIN EVENT — internal fanout messages whose type
    // name ends with "DomainEvent" (same convention AuthorizationMiddleware
    // uses). The Round 2 event-noise polish routes these to Debug level.
    private sealed class SampleDomainEvent;

    // Builds a Wolverine envelope with the supplied message. Use null to
    // exercise the "Unknown" request-name branch.
    private static Envelope BuildEnvelope(object? message)
        => message is null ? new Envelope() : new Envelope(message);

    // ── BeforeAsync: non-null message ────────────────────────────────

    [Fact]
    public async Task BeforeAsync_WithNonNullMessage_LogsInformationOnce()
    {
        // Arrange
        var logger = Substitute.For<ILogger<LoggingMiddleware>>();
        // The LoggerMessage source-generated delegate (CA1848 refactor)
        // gates the underlying ILogger.Log call on IsEnabled — and
        // NSubstitute's auto-value for bool is false, which would suppress
        // the call this test verifies. Enable logging explicitly.
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sut = new LoggingMiddleware(logger);
        var envelope = BuildEnvelope(new SampleCommand());

        // Act
        await sut.BeforeAsync(envelope, CancellationToken.None);

        // Assert
        // BeforeAsync calls _logger.LogInformation("Starting {RequestName}", requestName).
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task BeforeAsync_WithNonNullMessage_LogsStartingAndTypeName()
    {
        // Arrange
        // Capture the formatted log message via a real ILogger stub so we
        // can assert on the rendered string. NSubstitute's Received doesn't
        // let us match the formatted string directly because LogInformation
        // wraps the underlying Log call — we use a spy logger that captures
        // the formatter's output.
        var spy = new SpyLogger<LoggingMiddleware>();
        var sut = new LoggingMiddleware(spy);
        var envelope = BuildEnvelope(new SampleCommand());

        // Act
        await sut.BeforeAsync(envelope, CancellationToken.None);

        // Assert
        spy.LastMessage.Should().Contain("Starting");
        spy.LastMessage.Should().Contain(nameof(SampleCommand));
    }

    // ── BeforeAsync: null message ────────────────────────────────────

    [Fact]
    public async Task BeforeAsync_WithNullMessage_LogsStartingUnknown()
    {
        // Arrange
        var spy = new SpyLogger<LoggingMiddleware>();
        var sut = new LoggingMiddleware(spy);
        var envelope = BuildEnvelope(null);

        // Act
        await sut.BeforeAsync(envelope, CancellationToken.None);

        // Assert
        // envelope.Message is null → requestName = "Unknown".
        spy.LastMessage.Should().Contain("Starting");
        spy.LastMessage.Should().Contain("Unknown");
    }

    // ── AfterAsync: non-null message ─────────────────────────────────

    [Fact]
    public async Task AfterAsync_WithNonNullMessage_LogsInformationOnce()
    {
        // Arrange
        var logger = Substitute.For<ILogger<LoggingMiddleware>>();
        // See BeforeAsync_WithNonNullMessage_LogsInformationOnce: the
        // source-generated delegate checks IsEnabled before logging.
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sut = new LoggingMiddleware(logger);
        var envelope = BuildEnvelope(new SampleCommand());

        // Act
        await sut.AfterAsync(envelope, CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task AfterAsync_WithNonNullMessage_LogsCompletedAndTypeName()
    {
        // Arrange
        var spy = new SpyLogger<LoggingMiddleware>();
        var sut = new LoggingMiddleware(spy);
        var envelope = BuildEnvelope(new SampleCommand());

        // Act
        await sut.AfterAsync(envelope, CancellationToken.None);

        // Assert
        spy.LastMessage.Should().Contain("Completed");
        spy.LastMessage.Should().Contain(nameof(SampleCommand));
    }

    // ── AfterAsync: null message ─────────────────────────────────────

    [Fact]
    public async Task AfterAsync_WithNullMessage_LogsCompletedUnknown()
    {
        // Arrange
        var spy = new SpyLogger<LoggingMiddleware>();
        var sut = new LoggingMiddleware(spy);
        var envelope = BuildEnvelope(null);

        // Act
        await sut.AfterAsync(envelope, CancellationToken.None);

        // Assert
        spy.LastMessage.Should().Contain("Completed");
        spy.LastMessage.Should().Contain("Unknown");
    }

    // ── Domain events route to Debug (Round 2 event-noise polish) ────

    // The middleware policy applies to domain-event handler chains too.
    // Without the routing, every internal fanout message (SaleLineItemAdded,
    // NotificationCreated, …) logged "Starting X / Completed X" at
    // Information level — tripling log volume per user action. Domain
    // events now log at Debug; commands/queries stay at Information.

    [Fact]
    public async Task BeforeAsync_WithDomainEvent_LogsAtDebugNotInformation()
    {
        // Arrange
        var logger = Substitute.For<ILogger<LoggingMiddleware>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sut = new LoggingMiddleware(logger);
        var envelope = BuildEnvelope(new SampleDomainEvent());

        // Act
        await sut.BeforeAsync(envelope, CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
        logger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task AfterAsync_WithDomainEvent_LogsAtDebugNotInformation()
    {
        // Arrange
        var logger = Substitute.For<ILogger<LoggingMiddleware>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var sut = new LoggingMiddleware(logger);
        var envelope = BuildEnvelope(new SampleDomainEvent());

        // Act
        await sut.AfterAsync(envelope, CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Debug,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
        logger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task BeforeAsync_WithDomainEvent_SpyLoggerRecordsDebugLevel()
    {
        // Arrange — SpyLogger.IsEnabled returns true, so the Debug call
        // goes through and the spy records the level.
        var spy = new SpyLogger<LoggingMiddleware>();
        var sut = new LoggingMiddleware(spy);
        var envelope = BuildEnvelope(new SampleDomainEvent());

        // Act
        await sut.BeforeAsync(envelope, CancellationToken.None);

        // Assert
        spy.LastLogLevel.Should().Be(LogLevel.Debug);
        spy.LastMessage.Should().Contain("Starting");
        spy.LastMessage.Should().Contain(nameof(SampleDomainEvent));
    }

    [Fact]
    public async Task BeforeAsync_WithCommand_SpyLoggerRecordsInformationLevel()
    {
        // Arrange — commands/queries keep their Information level (guards
        // against the routing accidentally degrading ALL messages).
        var spy = new SpyLogger<LoggingMiddleware>();
        var sut = new LoggingMiddleware(spy);
        var envelope = BuildEnvelope(new SampleCommand());

        // Act
        await sut.BeforeAsync(envelope, CancellationToken.None);

        // Assert
        spy.LastLogLevel.Should().Be(LogLevel.Information);
    }

    // ── CancellationToken accepted ─────────────────────────────────────

    // The middleware accepts a CancellationToken but the body doesn't await
    // anything that observes it. We assert that passing a token (including
    // one that's already cancelled) does NOT throw and the call completes
    // successfully — this protects the contract that the method's signature
    // doesn't lie about its parameter usage.
    [Fact]
    public async Task BeforeAsync_WithAlreadyCancelledToken_DoesNotThrow()
    {
        // Arrange
        var spy = new SpyLogger<LoggingMiddleware>();
        var sut = new LoggingMiddleware(spy);
        var envelope = BuildEnvelope(new SampleCommand());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = async () => await sut.BeforeAsync(envelope, cts.Token);

        // Assert
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AfterAsync_WithAlreadyCancelledToken_DoesNotThrow()
    {
        // Arrange
        var spy = new SpyLogger<LoggingMiddleware>();
        var sut = new LoggingMiddleware(spy);
        var envelope = BuildEnvelope(new SampleCommand());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = async () => await sut.AfterAsync(envelope, cts.Token);

        // Assert
        await act.Should().NotThrowAsync();
    }
}

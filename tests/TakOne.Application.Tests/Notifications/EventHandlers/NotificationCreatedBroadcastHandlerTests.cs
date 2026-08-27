using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.EventHandlers;
using TakOne.Domain.Notifications.Events;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.EventHandlers;

/// <summary>
/// Unit tests for <see cref="NotificationCreatedBroadcastHandler"/>.
///
/// COVERAGE APPROACH:
///   The handler is a static method that wraps a SignalR broadcast call
///   in try/catch — failures are logged but never propagate (the persisted
///   Notification row is the source of truth; the live ping is best-effort).
///   <see cref="OperationCanceledException"/> is NOT caught (the
///   try/catch filter is `when (ex is not OperationCanceledException)`)
///   so cancellation propagates correctly through the Wolverine pipeline.
///
///   Tests cover:
///     • success path → broadcaster.BroadcastToUserAsync called once with event.UserId
///     • InvalidOperationException thrown by broadcaster → caught + warning logged
///     •OperationCanceledException thrown by broadcaster → NOT caught (propagates)
///     • TaskCanceledException (an OCE subtype) → propagates
///     • CancellationToken is forwarded to BroadcastToUserAsync
///     • null UserId (Guid.Empty) still calls broadcaster
///     • multiple events handled independently (no shared state)
/// </summary>
public class NotificationCreatedBroadcastHandlerTests
{
    // ── Helpers ───────────────────────────────────────────────────────

    // Builds a NSubstitute mock environment for the handler.
    private static (INotificationBroadcaster broadcaster, ILogger<NotificationCreatedBroadcastHandler> logger)
        BuildMocks()
    {
        var broadcaster = Substitute.For<INotificationBroadcaster>();
        broadcaster.BroadcastToUserAsync(default, default)
            .ReturnsForAnyArgs(Task.CompletedTask);
        var logger = Substitute.For<ILogger<NotificationCreatedBroadcastHandler>>();
        return (broadcaster, logger);
    }

    // Builds a NotificationCreatedDomainEvent with the supplied UserId.
    private static NotificationCreatedDomainEvent BuildEvent(Guid userId)
        => new(
            notificationId: TestValues.NotificationId,
            userId: userId,
            kind: 1,
            saleDisplayNumber: "INT-1403-00000001");

    // ── Success path ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenBroadcasterSucceeds_CallsBroadcastOnceWithUserId()
    {
        // Arrange
        var (broadcaster, logger) = BuildMocks();
        var @event = BuildEvent(TestValues.UserId);

        // Act
        await NotificationCreatedBroadcastHandler.HandleAsync(
            @event, broadcaster, logger, CancellationToken.None);

        // Assert
        await broadcaster.Received(1).BroadcastToUserAsync(
            Arg.Is(TestValues.UserId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenBroadcasterSucceeds_DoesNotLogWarning()
    {
        // Arrange
        var (broadcaster, logger) = BuildMocks();
        var @event = BuildEvent(TestValues.UserId);

        // Act
        await NotificationCreatedBroadcastHandler.HandleAsync(
            @event, broadcaster, logger, CancellationToken.None);

        // Assert
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Generic exception → caught + warning logged ─────────────────

    [Fact]
    public async Task HandleAsync_WhenBroadcasterThrowsInvalidOperationException_LogsWarning()
    {
        // Arrange
        var (broadcaster, logger) = BuildMocks();
        broadcaster.BroadcastToUserAsync(default, default)
            .ReturnsForAnyArgs(_ => Task.FromException(new InvalidOperationException("hub down")));
        var @event = BuildEvent(TestValues.UserId);

        // Act
        Func<Task> act = async () => await NotificationCreatedBroadcastHandler.HandleAsync(
            @event, broadcaster, logger, CancellationToken.None);

        // Assert
        // The exception must NOT propagate — broadcast is best-effort.
        await act.Should().NotThrowAsync();
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Fact]
    public async Task HandleAsync_WhenBroadcasterThrowsInvalidOperationException_WarningContainsUserId()
    {
        // Arrange
        // Use the spy logger to assert the rendered warning message
        // contains the user id placeholder.
        var broadcaster = Substitute.For<INotificationBroadcaster>();
        broadcaster.BroadcastToUserAsync(default, default)
            .ReturnsForAnyArgs(_ => Task.FromException(new InvalidOperationException("hub down")));
        var spy = new SpyLogger<NotificationCreatedBroadcastHandler>();
        var @event = BuildEvent(TestValues.UserId);

        // Act
        await NotificationCreatedBroadcastHandler.HandleAsync(
            @event, broadcaster, spy, CancellationToken.None);

        // Assert
        spy.LastLogLevel.Should().Be(LogLevel.Warning);
        spy.LastMessage.Should().Contain(TestValues.UserId.ToString());
        spy.LastMessage.Should().Contain("broadcast to user");
    }

    // ── OperationCanceledException → propagates ─────────────────────

    // The catch filter is `when (ex is not OperationCanceledException)`.
    // An OperationCanceledException thrown by the broadcaster must NOT be
    // caught — it must propagate so the Wolverine pipeline can clean up
    // the cancellation cleanly.
    [Fact]
    public async Task HandleAsync_WhenBroadcasterThrowsOperationCanceledException_Propagates()
    {
        // Arrange
        var (broadcaster, logger) = BuildMocks();
        var cts = new CancellationTokenSource();
        cts.Cancel();
        broadcaster.BroadcastToUserAsync(default, default)
            .ReturnsForAnyArgs(_ => Task.FromException(new OperationCanceledException(cts.Token)));
        var @event = BuildEvent(TestValues.UserId);

        // Act
        Func<Task> act = async () => await NotificationCreatedBroadcastHandler.HandleAsync(
            @event, broadcaster, logger, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        // The warning was NOT logged — the exception propagated past the catch.
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // TaskCanceledException is a subclass of OperationCanceledException.
    // The C# `is not OperationCanceledException` pattern-matches against
    // the runtime type, which catches derived types too — so
    // TaskCanceledException must ALSO propagate (it IS an OCE).
    [Fact]
    public async Task HandleAsync_WhenBroadcasterThrowsTaskCanceledException_Propagates()
    {
        // Arrange
        var (broadcaster, logger) = BuildMocks();
        broadcaster.BroadcastToUserAsync(default, default)
            .ReturnsForAnyArgs(_ => Task.FromException(new TaskCanceledException()));
        var @event = BuildEvent(TestValues.UserId);

        // Act
        Func<Task> act = async () => await NotificationCreatedBroadcastHandler.HandleAsync(
            @event, broadcaster, logger, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<TaskCanceledException>();
        logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── CancellationToken forwarding ─────────────────────────────────

    [Fact]
    public async Task HandleAsync_ForwardsCancellationTokenToBroadcaster()
    {
        // Arrange
        var (broadcaster, logger) = BuildMocks();
        var @event = BuildEvent(TestValues.UserId);
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await NotificationCreatedBroadcastHandler.HandleAsync(
            @event, broadcaster, logger, ct);

        // Assert
        await broadcaster.Received(1).BroadcastToUserAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Empty UserId still calls broadcaster ────────────────────────

    // A null/empty user id (Guid.Empty) is a degenerate case — the
    // broadcaster would no-op for it in production, but the handler itself
    // doesn't gate on the id. This protects the contract that the handler
    // doesn't second-guess the event payload.
    [Fact]
    public async Task HandleAsync_WithEmptyUserId_StillCallsBroadcaster()
    {
        // Arrange
        var (broadcaster, logger) = BuildMocks();
        var @event = BuildEvent(Guid.Empty);

        // Act
        await NotificationCreatedBroadcastHandler.HandleAsync(
            @event, broadcaster, logger, CancellationToken.None);

        // Assert
        await broadcaster.Received(1).BroadcastToUserAsync(
            Arg.Is(Guid.Empty),
            Arg.Any<CancellationToken>());
    }

    // ── Multiple events handled independently ───────────────────────

    // The handler is stateless (it's a static method), so calling it
    // twice with different events must produce two independent broadcast
    // calls — one per event's UserId. This guards against a future
    // refactor that caches the broadcaster result across calls.
    [Fact]
    public async Task HandleAsync_WithTwoEvents_CallsBroadcasterForEachEvent()
    {
        // Arrange
        var (broadcaster, logger) = BuildMocks();
        var userA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var userB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var eventA = BuildEvent(userA);
        var eventB = BuildEvent(userB);

        // Act
        await NotificationCreatedBroadcastHandler.HandleAsync(
            eventA, broadcaster, logger, CancellationToken.None);
        await NotificationCreatedBroadcastHandler.HandleAsync(
            eventB, broadcaster, logger, CancellationToken.None);

        // Assert
        await broadcaster.Received(1).BroadcastToUserAsync(
            Arg.Is(userA), Arg.Any<CancellationToken>());
        await broadcaster.Received(1).BroadcastToUserAsync(
            Arg.Is(userB), Arg.Any<CancellationToken>());
    }
}

/// <summary>
/// Spy implementation of <see cref="ILogger{TCategoryName}"/> for the
/// broadcast handler tests. Same pattern as the middleware tests — needed
/// to assert on the rendered warning message string.
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

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Queries.GetUnreadNotificationCount;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Queries.GetUnreadNotificationCount;

/// <summary>
/// Unit tests for <see cref="GetUnreadNotificationCountQueryHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// (parameterless) query, the current-user service, the notification
/// repository, a logger, and a cancellation token. It returns a bare
/// <c>int</c> (NOT wrapped in <c>Result&lt;int&gt;</c>) — auth failure
/// returns 0 (silent — UI shows no badge). This matches the contract
/// documented on the query file.
///
/// COVERAGE TARGETS:
///   1. Authenticated happy path → returns the repository's count.
///   2. Auth failure → returns 0 (silent).
///   3. The repository call receives the current user's Id (anti-CSRF).
///   4. The success path does NOT log an Information entry — the handler
///      is read-only, the warning-on-auth-reject is the only log call.
///   5. Cancellation token forwarded.
/// </summary>
public class GetUnreadNotificationCountQueryHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static GetUnreadNotificationCountQuery BuildValidQuery()
        => new();

    private static (
        ICurrentUserService currentUser,
        INotificationRepository notificationRepo,
        ILogger<GetUnreadNotificationCountQueryHandler> logger)
        BuildMocks(int unreadCount = 7)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        var notificationRepo = Substitute.For<INotificationRepository>();
        notificationRepo.GetUnreadCountAsync(default, default)
            .ReturnsForAnyArgs(unreadCount);

        var logger = Substitute.For<ILogger<GetUnreadNotificationCountQueryHandler>>();

        return (currentUser, notificationRepo, logger);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAuthenticated_ReturnsRepositoryCount()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks(unreadCount: 5);

        // Act
        var result = await GetUnreadNotificationCountQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        // The handler returns a bare int (NOT wrapped in Result<int>).
        result.Should().Be(5);
    }

    [Fact]
    public async Task HandleAsync_WhenRepositoryReturnsZero_ReturnsZero()
    {
        // Arrange
        // E.g. a user with no unread notifications — the UI shows no
        // badge. This is the steady-state after the user reads all
        // notifications.
        var (currentUser, notificationRepo, logger) = BuildMocks(unreadCount: 0);

        // Act
        var result = await GetUnreadNotificationCountQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenAuthenticated_CallsRepositoryWithCurrentUserId()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks();

        // Act
        await GetUnreadNotificationCountQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetUnreadCountAsync(
            Arg.Is<Guid>(id => id == TestValues.CreatedByUserId),
            Arg.Any<CancellationToken>());
    }

    // ── Auth failure → returns 0 (silent) ─────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsZero()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await GetUnreadNotificationCountQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        // Auth failure returns 0 (silent — UI shows no badge). NOT
        // Result.Failure(0) — the int return is bare.
        result.Should().Be(0);
        // Repo is NOT called on the auth-fail path.
        await notificationRepo.DidNotReceive().GetUnreadCountAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await GetUnreadNotificationCountQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToRepository()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await GetUnreadNotificationCountQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger, ct);

        // Assert
        await notificationRepo.Received(1).GetUnreadCountAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

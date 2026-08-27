using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Commands.MarkAllNotificationsAsRead;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Commands.MarkAllNotificationsAsRead;

/// <summary>
/// Unit tests for <see cref="MarkAllNotificationsAsReadCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// (parameterless) command, the current-user service, the notification
/// repository, a logger, and a cancellation token. We mock every
/// collaborator with NSubstitute. The repository's
/// <c>MarkAllAsReadAsync(userId, ct)</c> returns the int count of rows
/// affected by the bulk UPDATE — the handler forwards this as
/// <c>Result&lt;int&gt;.Success(affected)</c>.
///
/// SPECIAL CASES:
///   1. The handler uses the standard "Authentication required." string
///      (NOT a stable error code from NotificationErrors — this is the
///      only Notification handler that does NOT use the stable-code
///      catalog for its auth failure). Returns Result&lt;int&gt;.Failure
///      with the literal string.
///   2. The scope guard: the userId comes from currentUser.UserId (NOT
///      from the command — the command is parameterless). Anti-CSRF.
///   3. The success path DOES log an Information entry with the
///      affected count + userId. (The auth-reject path does NOT log —
///      silent failure, same as MarkNotificationAsReadCommandHandler.)
/// </summary>
public class MarkAllNotificationsAsReadCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // The command is parameterless — there's no input to mutate.
    private static MarkAllNotificationsAsReadCommand BuildValidCommand()
        => new();

    private static (
        ICurrentUserService currentUser,
        INotificationRepository notificationRepo,
        ILogger<MarkAllNotificationsAsReadCommandHandler> logger)
        BuildMocks(int markAllAsReadResult = 3)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        var notificationRepo = Substitute.For<INotificationRepository>();
        notificationRepo.MarkAllAsReadAsync(default, default)
            .ReturnsForAnyArgs(markAllAsReadResult);

        var logger = Substitute.For<ILogger<MarkAllNotificationsAsReadCommandHandler>>();

        return (currentUser, notificationRepo, logger);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAuthenticated_ReturnsSuccessWithAffectedCount()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks(markAllAsReadResult: 5);

        // Act
        var result = await MarkAllNotificationsAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(5);
    }

    // The handler forwards the affected count straight through from the
    // repository. The UI uses this count to display "Marked N
    // notification(s) as read" — verifying the forwarding protects the
    // UI's contract from silent zero-ing.
    [Fact]
    public async Task HandleAsync_WhenRepositoryReturnsZero_ReturnsSuccessWithZero()
    {
        // Arrange
        // E.g. a user with no unread notifications → the bulk UPDATE
        // affects 0 rows. The handler must still return Success(0)
        // (NOT Failure) — idempotent.
        var (currentUser, notificationRepo, logger) = BuildMocks(markAllAsReadResult: 0);

        // Act
        var result = await MarkAllNotificationsAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenAuthenticated_CallsMarkAllAsReadAsyncOnceWithCurrentUserId()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks();

        // Act
        await MarkAllNotificationsAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).MarkAllAsReadAsync(
            Arg.Is<Guid>(id => id == TestValues.CreatedByUserId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_LogsInformationWithAffectedCount()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks(markAllAsReadResult: 7);

        // Act
        await MarkAllNotificationsAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Auth rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await MarkAllNotificationsAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        // This handler uses the LITERAL string "Authentication required."
        // (NOT a stable error code — unlike MarkNotificationAsReadCommand
        // which uses NotificationErrors.FormatAuthRequired()).
        result.Error.Should().Be("Authentication required.");
        // Auth rejection must short-circuit BEFORE the repo call.
        await notificationRepo.DidNotReceive().MarkAllAsReadAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await MarkAllNotificationsAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToMarkAllAsReadAsync()
    {
        // Arrange
        var (currentUser, notificationRepo, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await MarkAllNotificationsAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, logger, ct);

        // Assert
        await notificationRepo.Received(1).MarkAllAsReadAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

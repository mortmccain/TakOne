using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Commands.DeleteNotification;
using TakOne.Application.Notifications.Errors;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Commands.DeleteNotification;

/// <summary>
/// Unit tests for <see cref="DeleteNotificationCommandHandler"/> (Round 4 —
/// the per-notification dismiss).
/// </summary>
/// <remarks>
/// The handler delegates the scoped delete to
/// <see cref="INotificationRepository.DeleteForUserAsync"/> and maps the
/// boolean outcome to a <see cref="Result"/>: deleted → success; zero
/// rows (missing OR foreign notification — indistinguishable by design)
/// → the stable <see cref="NotificationErrors.FormatNotFound"/> code.
/// </remarks>
public class DeleteNotificationCommandHandlerTests
{
    private static DeleteNotificationCommand BuildValidCommand(Guid? notificationId = null)
        => new(notificationId ?? TestValues.NotificationId);

    private static (ICurrentUserService currentUser, INotificationRepository repo, ILogger<DeleteNotificationCommandHandler> logger)
        BuildMocks(bool authenticated = true)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(authenticated);
        currentUser.UserId.Returns(TestValues.UserId);

        var repo = Substitute.For<INotificationRepository>();
        repo.DeleteForUserAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var logger = Substitute.For<ILogger<DeleteNotificationCommandHandler>>();
        return (currentUser, repo, logger);
    }

    [Fact]
    public async Task HandleAsync_WhenDeleted_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, repo, logger) = BuildMocks();
        Guid? capturedNotificationId = null;
        Guid? capturedUserId = null;
        repo.DeleteForUserAsync(
                Arg.Do<Guid>(id => capturedNotificationId = id),
                Arg.Do<Guid>(id => capturedUserId = id),
                Arg.Any<CancellationToken>())
            .Returns(true);

        // Act
        var result = await DeleteNotificationCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, repo, logger, CancellationToken.None);

        // Assert — success, and the scope (user id) came from the
        // current-user service, never from the command.
        result.IsSuccess.Should().BeTrue();
        capturedNotificationId.Should().Be(TestValues.NotificationId);
        capturedUserId.Should().Be(TestValues.UserId);
    }

    [Fact]
    public async Task HandleAsync_WhenNoRowDeleted_ReturnsNotFoundFailure()
    {
        // Arrange — the repo reports zero rows: the id is missing OR
        // belongs to another user (the handler can't tell, by design).
        var (currentUser, repo, logger) = BuildMocks();
        repo.DeleteForUserAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var result = await DeleteNotificationCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, repo, logger, CancellationToken.None);

        // Assert — the stable anti-enumeration error code.
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(NotificationErrors.FormatNotFound());
    }

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthRequired()
    {
        // Arrange — defense-in-depth: [RequireAuthentication] should have
        // rejected the message before the handler; the handler re-checks.
        var (currentUser, repo, logger) = BuildMocks(authenticated: false);

        // Act
        var result = await DeleteNotificationCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, repo, logger, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(NotificationErrors.FormatAuthRequired());
        await repo.DidNotReceive().DeleteForUserAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}

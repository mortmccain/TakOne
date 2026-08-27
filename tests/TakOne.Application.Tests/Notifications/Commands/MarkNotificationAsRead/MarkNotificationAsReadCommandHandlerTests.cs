using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Commands.MarkNotificationAsRead;
using TakOne.Application.Notifications.Errors;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Commands.MarkNotificationAsRead;

/// <summary>
/// Unit tests for <see cref="MarkNotificationAsReadCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the current-user service, the notification repository, the
/// unit of work, a logger, and a cancellation token. We mock every
/// collaborator with NSubstitute. The repository returns a REAL
/// <see cref="Notification"/> instance (built via the public
/// <see cref="Notification.Create"/> factory) so we can observe the
/// side-effect of <c>MarkAsRead()</c> on <c>ReadAtUtc</c>.
///
/// SPECIAL CASES:
///   1. The handler uses the stable-code error catalog
///      <see cref="NotificationErrors"/> (NOT hardcoded English) so
///      the UI can localize. The auth-reject error code is the literal
///      string "NotificationAuthRequired"; the not-found error code is
///      "NotificationNotFound".
///   2. The scope guard: the repository's
///      <c>GetByIdForUserAsync(notificationId, userId, ct)</c> takes
///      the userId from <c>currentUser.UserId</c> (NOT from the
///      command) — anti-CSRF. A caller passing someone else's
///      notificationId gets null (same shape as missing), then 404.
///   3. The auth-reject path does NOT log a warning (unlike the
///      Category handlers) — the handler returns the stable error code
///      silently. The not-found path DOES log a warning.
///   4. MarkAsRead is idempotent — calling it on an already-read
///      notification is a no-op (ReadAtUtc stays set to its previous
///      value; no exception, no spurious UPDATE logic).
/// </summary>
public class MarkNotificationAsReadCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static MarkNotificationAsReadCommand BuildValidCommand(Guid? notificationId = null)
        => new(notificationId ?? TestValues.NotificationId);

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - notificationRepository.GetByIdForUserAsync returns a real
    //     Notification built via Notification.Create (the public factory)
    //     — ReadAtUtc starts null so we can observe MarkAsRead's side-
    //     effect.
    //   - unitOfWork.SaveChangesAsync returns 1
    private static (
        ICurrentUserService currentUser,
        INotificationRepository notificationRepo,
        IUnitOfWork unitOfWork,
        ILogger<MarkNotificationAsReadCommandHandler> logger,
        Notification notification)
        BuildMocks(Notification? notification = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a REAL Notification so we can observe the MarkAsRead side-
        // effect on ReadAtUtc. The public Create factory sets
        // ReadAtUtc=null initially — perfect for verifying the mutation.
        var actualNotification = notification
            ?? Notification.Create(
                userId: TestValues.CreatedByUserId,
                kind: NotificationKind.SaleSubmitted,
                saleId: TestValues.SaleId,
                saleDisplayNumber: "INT-1505-00000042",
                actorName: "Approver Name",
                reason: null);

        var notificationRepo = Substitute.For<INotificationRepository>();
        notificationRepo.GetByIdForUserAsync(default, default, default)
            .ReturnsForAnyArgs(actualNotification);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<MarkNotificationAsReadCommandHandler>>();

        return (currentUser, notificationRepo, unitOfWork, logger, actualNotification);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotificationExists_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        var result = await MarkNotificationAsReadCommandHandler.HandleAsync(
            command, currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenNotificationExists_SetsReadAtUtc()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, notification) = BuildMocks();
        var command = BuildValidCommand();
        // Sanity: starts unread.
        notification.ReadAtUtc.Should().BeNull();

        // Act
        await MarkNotificationAsReadCommandHandler.HandleAsync(
            command, currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        notification.ReadAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();

        // Act
        await MarkNotificationAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Auth rejection (returns stable error code, NOT English) ──────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsNotificationAuthRequired()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await MarkNotificationAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        // Stable error code (NOT hardcoded English) so the UI can localize.
        result.Error.Should().Be(NotificationErrors.FormatAuthRequired());
        result.Error.Should().Be("NotificationAuthRequired");
        // Auth rejection must short-circuit BEFORE any repo / UoW call.
        await notificationRepo.DidNotReceive().GetByIdForUserAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsNotificationAuthRequired()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await MarkNotificationAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NotificationAuthRequired");
    }

    // ── Scope guard: userId comes from currentUser, NOT the command ──

    // The repository's GetByIdForUserAsync takes BOTH the command's
    // NotificationId AND currentUser.UserId. The userId comes from the
    // auth context — the command doesn't even HAVE a UserId property
    // (anti-CSRF: the caller cannot supply a different user Id).
    [Fact]
    public async Task HandleAsync_WhenNotificationExists_PassesCurrentUserIdToRepository()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        await MarkNotificationAsReadCommandHandler.HandleAsync(
            command, currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetByIdForUserAsync(
            Arg.Any<Guid>(),
            Arg.Is<Guid>(id => id == TestValues.CreatedByUserId),
            Arg.Any<CancellationToken>());
    }

    // ── Not found (also: anti-enumeration — same error for missing AND
    //    wrong-user). ──────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotificationNotFound_ReturnsNotificationNotFound()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();
        notificationRepo.GetByIdForUserAsync(default, default, default)
            .ReturnsForAnyArgs((Notification?)null);

        // Act
        var result = await MarkNotificationAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        // Stable error code — same shape whether the notification is
        // genuinely missing OR doesn't belong to the caller (anti-
        // enumeration: don't leak "this notification belongs to someone
        // else").
        result.Error.Should().Be(NotificationErrors.FormatNotFound());
        result.Error.Should().Be("NotificationNotFound");
        // Not-found short-circuits BEFORE MarkAsRead + SaveChanges.
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenNotificationNotFound_LogsWarning()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();
        notificationRepo.GetByIdForUserAsync(default, default, default)
            .ReturnsForAnyArgs((Notification?)null);

        // Act
        await MarkNotificationAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // The auth-reject path does NOT log a warning (the SUT returns the
    // stable error code silently — defense-in-depth, no logger call).
    // We lock this in: if someone refactors to add a warning log, this
    // test will fail and force them to update the test suite.
    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_DoesNotLog()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await MarkNotificationAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // Zero log calls of ANY level on the auth-reject path.
        logger.DidNotReceive().Log(
            Arg.Any<LogLevel>(),
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Idempotency: MarkAsRead on an already-read notification is a no-op.

    // The domain's MarkAsRead method sets ReadAtUtc only if it's null.
    // Calling the handler on an already-read notification must succeed
    // (no exception, no spurious state mutation) and still call
    // SaveChanges (the round-trip is a null-op).
    [Fact]
    public async Task HandleAsync_WhenNotificationIsAlreadyRead_StaysRead()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, notification) = BuildMocks();
        // Pre-mark as read via the domain's public method.
        notification.MarkAsRead();
        var firstReadAt = notification.ReadAtUtc!.Value;
        // Sanity: ReadAtUtc is set before the handler runs (the !
        // de-null above would have thrown if it weren't).

        // Act
        var result = await MarkNotificationAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // ReadAtUtc should NOT be re-set — the domain's MarkAsRead is
        // idempotent (no-op when already read).
        notification.ReadAtUtc.Should().Be(firstReadAt);
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToGetByIdForUserAsync()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await MarkNotificationAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, unitOfWork, logger, ct);

        // Assert
        await notificationRepo.Received(1).GetByIdForUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToSaveChangesAsync()
    {
        // Arrange
        var (currentUser, notificationRepo, unitOfWork, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await MarkNotificationAsReadCommandHandler.HandleAsync(
            BuildValidCommand(), currentUser, notificationRepo, unitOfWork, logger, ct);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

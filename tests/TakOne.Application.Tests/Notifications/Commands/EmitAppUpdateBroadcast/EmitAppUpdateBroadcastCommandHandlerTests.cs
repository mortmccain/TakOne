using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Commands;
using TakOne.Application.Notifications.Commands.EmitAppUpdateBroadcast;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Commands.EmitAppUpdateBroadcast;

/// <summary>
/// Unit tests for <see cref="EmitAppUpdateBroadcastCommandHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// command, the user repository, the broadcast-notification repository,
/// the notification repository, the unit of work, a logger, and a
/// cancellation token. It returns <see cref="Result{Int32}"/>. We mock
/// every collaborator with NSubstitute — INCLUDING the static
/// <see cref="BroadcastFanout"/> helper is not directly mockable, so we
/// let the real fanout code path execute against mocked repositories +
/// unit-of-work and assert on the mocks the fanout uses internally.
///
/// SPECIAL CASES:
///   1. The handler performs NO auth check (system-internal — the
///      command is marked [RequireSystemInternal] and dispatched by the
///      AppUpdateBroadcasterHostedService). The handler does not even
///      take an ICurrentUserService parameter.
///   2. Idempotency dedup: before fanning out, the handler checks
///      whether a BroadcastNotification audit row with the same
///      (Title, FanoutKind=AppUpdate) already exists. If yes, it skips
///      the fanout and returns the existing RecipientCount (Wolverine
///      redelivery guard).
///   3. If no existing audit row, delegates to BroadcastFanout.ExecuteAsync
///      with sentByUserId=Guid.Empty, scope=All, all targets null,
///      fanoutKind=AppUpdate.
/// </summary>
public class EmitAppUpdateBroadcastCommandHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static EmitAppUpdateBroadcastCommand BuildValidCommand(
        string? title = null,
        string? message = null)
        => new(
            title ?? "TakOne updated to v1.2.3",
            message ?? "Please reload the app to get the latest features.");

    // Builds a fully-wired NSubstitute environment with the dedup check
    // returning null (no existing audit row → fanout will run). Each
    // test can override the dedup check to return a real audit row
    // (dedup hit → fanout skipped).
    private static (
        IUserRepository userRepo,
        IBroadcastNotificationRepository broadcastRepo,
        INotificationRepository notificationRepo,
        IUnitOfWork unitOfWork,
        ILogger<EmitAppUpdateBroadcastCommandHandler> logger)
        BuildMocks(BroadcastNotification? existing = null)
    {
        var userRepo = Substitute.For<IUserRepository>();
        // Default: Scope=All fanout returns 3 recipients.
        userRepo.GetAllActiveUserIdsAsync(default)
            .ReturnsForAnyArgs(new List<Guid>
            {
                TestValues.CreatedByUserId,
                TestValues.CustomerId,
                TestValues.UserId
            });

        var broadcastRepo = Substitute.For<IBroadcastNotificationRepository>();
        // Default: no existing audit row (dedup miss → fanout runs).
        broadcastRepo.GetByTitleAndKindAsync(string.Empty, default, default)
            .ReturnsForAnyArgs(existing);

        var notificationRepo = Substitute.For<INotificationRepository>();

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(default).ReturnsForAnyArgs(1);

        var logger = Substitute.For<ILogger<EmitAppUpdateBroadcastCommandHandler>>();

        return (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger);
    }

    // ── Dedup hit path ──────────────────────────────────────────────────

    // When a BroadcastNotification with the same (Title, AppUpdate)
    // already exists, the handler skips the fanout and returns the
    // existing RecipientCount. This prevents duplicate "app updated"
    // notifications when Wolverine's durable outbox redelivers an
    // unacked command (process crash between commit and worker ack).
    [Fact]
    public async Task HandleAsync_WhenAuditRowAlreadyExists_ReturnsExistingRecipientCount()
    {
        // Arrange
        // Build a real BroadcastNotification with RecipientCount=42.
        var existing = BroadcastNotification.Create(
            sentByUserId: Guid.Empty,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "TakOne updated to v1.2.3",
            message: "Please reload the app.",
            fanoutKind: NotificationKind.AppUpdate,
            recipientCount: 42);
        var (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger) = BuildMocks(existing);
        var command = BuildValidCommand(title: "TakOne updated to v1.2.3");

        // Act
        var result = await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
            command, userRepo, broadcastRepo, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42); // The existing RecipientCount.
    }

    // The dedup-hit path MUST NOT call the fanout — no audit row insert,
    // no per-user Notification rows. We lock this in: a refactor that
    // accidentally runs the fanout even on dedup hit would create
    // duplicate notifications for every user.
    [Fact]
    public async Task HandleAsync_WhenAuditRowAlreadyExists_DoesNotCallBroadcastRepoAddAsync()
    {
        // Arrange
        var existing = BroadcastNotification.Create(
            sentByUserId: Guid.Empty,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "TakOne updated to v1.2.3",
            message: "Please reload the app.",
            fanoutKind: NotificationKind.AppUpdate,
            recipientCount: 42);
        var (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger) = BuildMocks(existing);
        var command = BuildValidCommand(title: "TakOne updated to v1.2.3");

        // Act
        await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
            command, userRepo, broadcastRepo, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // No new audit row, no fanout rows.
        await broadcastRepo.DidNotReceive().AddAsync(
            Arg.Any<BroadcastNotification>(), Arg.Any<CancellationToken>());
        await notificationRepo.DidNotReceive().AddAsync(
            Arg.Any<Domain.Notifications.Entities.Notification>(),
            Arg.Any<CancellationToken>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // The dedup-hit path MUST NOT call the user repository — no need to
    // resolve recipients when the fanout is skipped.
    [Fact]
    public async Task HandleAsync_WhenAuditRowAlreadyExists_DoesNotCallUserRepo()
    {
        // Arrange
        var existing = BroadcastNotification.Create(
            sentByUserId: Guid.Empty,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "TakOne updated to v1.2.3",
            message: "Please reload the app.",
            fanoutKind: NotificationKind.AppUpdate,
            recipientCount: 42);
        var (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger) = BuildMocks(existing);
        var command = BuildValidCommand(title: "TakOne updated to v1.2.3");

        // Act
        await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
            command, userRepo, broadcastRepo, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // The fanout would have called GetAllActiveUserIdsAsync — that
        // MUST NOT fire on the dedup-hit path.
        await userRepo.DidNotReceive().GetAllActiveUserIdsAsync(Arg.Any<CancellationToken>());
    }

    // The dedup-hit path logs an Information entry with the existing
    // audit row's Id + RecipientCount + Title — for the audit trail.
    [Fact]
    public async Task HandleAsync_WhenAuditRowAlreadyExists_LogsInformation()
    {
        // Arrange
        var existing = BroadcastNotification.Create(
            sentByUserId: Guid.Empty,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "TakOne updated to v1.2.3",
            message: "Please reload the app.",
            fanoutKind: NotificationKind.AppUpdate,
            recipientCount: 42);
        var (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger) = BuildMocks(existing);
        var command = BuildValidCommand(title: "TakOne updated to v1.2.3");

        // Act
        await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
            command, userRepo, broadcastRepo, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Dedup miss path (fanout runs) ─────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNoExistingAuditRow_DelegatesToFanoutAndReturnsRecipientCount()
    {
        // Arrange
        // Default BuildMocks returns null for GetByTitleAndKindAsync
        // (dedup miss → fanout runs).
        var (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger) = BuildMocks();
        var command = BuildValidCommand();

        // Act
        var result = await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
            command, userRepo, broadcastRepo, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        // 3 recipients configured in BuildMocks's GetAllActiveUserIdsAsync.
        result.Value.Should().Be(3);
    }

    // The dedup-miss path MUST call the dedup check first — passing the
    // command's Title + NotificationKind.AppUpdate to the repository.
    [Fact]
    public async Task HandleAsync_WhenNoExistingAuditRow_CallsGetByTitleAndKindAsyncWithAppUpdateKind()
    {
        // Arrange
        var (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger) = BuildMocks();
        var command = BuildValidCommand(title: "TakOne updated to v9.9.9");

        // Act
        await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
            command, userRepo, broadcastRepo, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await broadcastRepo.Received(1).GetByTitleAndKindAsync(
            Arg.Is<string>(t => t == "TakOne updated to v9.9.9"),
            Arg.Is<NotificationKind>(k => k == NotificationKind.AppUpdate),
            Arg.Any<CancellationToken>());
    }

    // The dedup-miss path MUST call SaveChangesAsync once — the fanout
    // helper persists the audit row + N per-user Notification rows
    // atomically via Wolverine's AutoApplyTransactions middleware.
    [Fact]
    public async Task HandleAsync_WhenNoExistingAuditRow_CallsSaveChangesAsyncOnce()
    {
        // Arrange
        var (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger) = BuildMocks();

        // Act
        await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
            BuildValidCommand(), userRepo, broadcastRepo, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // The dedup-miss path logs an Information entry with the recipient
    // count + Title — for the audit trail.
    [Fact]
    public async Task HandleAsync_WhenNoExistingAuditRow_LogsInformation()
    {
        // Arrange
        var (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger) = BuildMocks();

        // Act
        await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
            BuildValidCommand(), userRepo, broadcastRepo, notificationRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        // TWO Information log calls on the dedup-miss path:
        //   1. BroadcastFanout.ExecuteAsync calls logger.LogInformation
        //      when the fanout completes (its own audit log line).
        //   2. The handler's own logger.LogInformation AFTER the fanout
        //      returns (its summary line "AppUpdate broadcast fanned
        //      out to N recipient(s)").
        // We assert >= 1 Information call to lock in the success-path
        // logging behavior without being too strict on whether the
        // fanout's internal log + the handler's log both fire (a
        // refactor that moves the fanout's log to the handler would
        // drop the count to 1, which would break this — by design).
        logger.Received(2).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToGetByTitleAndKindAsync()
    {
        // Arrange
        var (userRepo, broadcastRepo, notificationRepo, unitOfWork, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await EmitAppUpdateBroadcastCommandHandler.HandleAsync(
            BuildValidCommand(), userRepo, broadcastRepo, notificationRepo, unitOfWork, logger, ct);

        // Assert
        await broadcastRepo.Received(1).GetByTitleAndKindAsync(
            Arg.Any<string>(),
            Arg.Any<NotificationKind>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.DTOs;
using TakOne.Application.Notifications.Queries.GetBroadcastNotifications;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Queries.GetBroadcastNotifications;

/// <summary>
/// Unit tests for <see cref="GetBroadcastNotificationsQueryHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// query, the current-user service, the broadcast-notification
/// repository, the user repository, the customer-group repository,
/// a logger, and a cancellation token. It returns a bare
/// <see cref="PaginatedResult{BroadcastNotificationDto}"/> (NOT
/// wrapped in <c>Result&lt;&gt;</c>) — admin-role failure returns an
/// EMPTY page (warning logged, silent).
///
/// SPECIAL CASES:
///   1. Defense-in-depth admin-role check: non-admins (even if
///      authenticated) get an empty page. The [RequireRoles(Admin)]
///      attribute should already reject non-admins via Wolverine
///      middleware; this is the second layer.
///   2. Page clamps (MaxPageSize=100, default 20) — same pattern as
///      GetNotificationsForUserQueryHandler.
///   3. Batched name resolution: collects distinct sender + target user
///      Ids + target group Ids, makes ONE batched user lookup + ONE
///      batched group lookup, then projects with the dictionaries.
///   4. System-emitted broadcasts (SentByUserId == Guid.Empty) get
///      SentByUserName = null (the UI renders "System" instead of a
///      name).
///   5. Deleted-sender / deleted-group / deleted-target-user cases:
///      the dictionary lookup simply yields no entry → the DTO's
///      name field stays null. The UI's fallback paths render "Unknown".
/// </summary>
public class GetBroadcastNotificationsQueryHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private const int ExpectedMaxPageSize = 100;

    private static GetBroadcastNotificationsQuery BuildValidQuery(
        int? pageNumber = null,
        int? pageSize = null)
        => new()
        {
            PageNumber = pageNumber ?? 1,
            PageSize = pageSize ?? 20
        };

    // Builds a fully-wired NSubstitute environment with the current user
    // as Admin and an empty page from the broadcast repo. Each test can
    // override individual mock calls to inject a specific page.
    private static (
        ICurrentUserService currentUser,
        IBroadcastNotificationRepository broadcastRepo,
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        ILogger<GetBroadcastNotificationsQueryHandler> logger)
        BuildMocks(PaginatedResult<BroadcastNotification>? page = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);
        currentUser.IsInRole(Roles.Admin).Returns(true);

        var emptyPage = new PaginatedResult<BroadcastNotification>(
            Array.Empty<BroadcastNotification>(), 0, 1, 20);
        var actualPage = page ?? emptyPage;

        var broadcastRepo = Substitute.For<IBroadcastNotificationRepository>();
        broadcastRepo.GetPaginatedAsync(default, default, default)
            .ReturnsForAnyArgs(actualPage);

        // Default: empty list lookups for the batched name resolution.
        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdsReadOnlyAsync(default!, default)
            .ReturnsForAnyArgs(new List<User>());

        var groupRepo = Substitute.For<ICustomerGroupRepository>();
        groupRepo.GetByIdsReadOnlyAsync(default!, default)
            .ReturnsForAnyArgs(new List<CustomerGroup>());

        var logger = Substitute.For<ILogger<GetBroadcastNotificationsQueryHandler>>();

        return (currentUser, broadcastRepo, userRepo, groupRepo, logger);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAdminAndPageEmpty_ReturnsEmptyPage()
    {
        // Arrange
        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks();

        // Act
        var result = await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, broadcastRepo, userRepo, groupRepo, logger,
            CancellationToken.None);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    // ── Auth failure → empty page (silent) ────────────────────────────

    // Non-admin caller (even if authenticated) gets an empty page. This
    // is defense-in-depth — the [RequireRoles(Admin)] attribute should
    // already reject non-admins via Wolverine's middleware; this is the
    // second layer.
    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsEmptyPage()
    {
        // Arrange
        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, broadcastRepo, userRepo, groupRepo, logger,
            CancellationToken.None);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        // Repo is NOT called on the auth-fail path.
        await broadcastRepo.DidNotReceive().GetPaginatedAsync(
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // Authenticated but NOT admin → empty page (defense-in-depth).
    [Fact]
    public async Task HandleAsync_WhenNotAdmin_ReturnsEmptyPage()
    {
        // Arrange
        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks();
        currentUser.IsInRole(Roles.Admin).Returns(false);
        // Even if they're in some other role, the admin check fails.
        currentUser.IsInRole(Roles.Manager).Returns(true);

        // Act
        var result = await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, broadcastRepo, userRepo, groupRepo, logger,
            CancellationToken.None);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, broadcastRepo, userRepo, groupRepo, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Page clamping ───────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenPageNumberIsZero_ClampsToOne()
    {
        // Arrange
        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks();

        // Act
        await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(pageNumber: 0), currentUser, broadcastRepo, userRepo, groupRepo, logger,
            CancellationToken.None);

        // Assert
        await broadcastRepo.Received(1).GetPaginatedAsync(
            Arg.Is<int>(p => p == 1),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenPageSizeExceedsMax_ClampsToHundred()
    {
        // Arrange
        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks();

        // Act
        await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(pageSize: 500), currentUser, broadcastRepo, userRepo, groupRepo, logger,
            CancellationToken.None);

        // Assert
        await broadcastRepo.Received(1).GetPaginatedAsync(
            Arg.Any<int>(),
            Arg.Is<int>(p => p == ExpectedMaxPageSize),
            Arg.Any<CancellationToken>());
    }

    // ── System-emitted broadcasts: SentByUserName = null ─────────────

    // When SentByUserId == Guid.Empty (a system-emitted AppUpdate broadcast),
    // the handler skips the user lookup and sets SentByUserName = null
    // (the UI renders "System" instead of a person's name).
    [Fact]
    public async Task HandleAsync_WhenBroadcastIsSystemEmitted_SetsSentByUserNameToNull()
    {
        // Arrange
        // Build a BroadcastNotification with SentByUserId = Guid.Empty
        // (system-emitted AppUpdate).
        var systemBroadcast = BroadcastNotification.Create(
            sentByUserId: Guid.Empty,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "App updated to v1.2.3",
            message: "Please reload the app to get the latest features.",
            fanoutKind: NotificationKind.AppUpdate,
            recipientCount: 42);

        var page = new PaginatedResult<BroadcastNotification>(
            new[] { systemBroadcast }, totalCount: 1, pageNumber: 1, pageSize: 20);

        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks(page);

        // Act
        var result = await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, broadcastRepo, userRepo, groupRepo, logger,
            CancellationToken.None);

        // Assert
        var dto = result.Items.Should().ContainSingle().Subject;
        dto.SentByUserId.Should().Be(Guid.Empty);
        dto.SentByUserName.Should().BeNull();
        // The user repo's batched lookup must NOT be called — there's
        // no sender user Id to look up (Guid.Empty is excluded by the
        // senderUserIds filter in the SUT).
        await userRepo.DidNotReceive().GetByIdsReadOnlyAsync(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    // ── Batched name resolution: admin-authored broadcasts ────────────

    // The handler collects distinct sender + target user Ids + target
    // group Ids across the page, does ONE batched user lookup + ONE
    // batched group lookup, then projects with the dictionaries. This
    // avoids the N+1 of per-row lookups.
    [Fact]
    public async Task HandleAsync_WhenBroadcastIsAdminAuthored_ResolvesSenderNameFromUserRepo()
    {
        // Arrange
        // Build a BroadcastNotification sent by TestValues.CreatedByUserId.
        var adminBroadcast = BroadcastNotification.Create(
            sentByUserId: TestValues.CreatedByUserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "Welcome to TakOne!",
            message: "Glad to have you on board.",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 100);
        var page = new PaginatedResult<BroadcastNotification>(
            new[] { adminBroadcast }, totalCount: 1, pageNumber: 1, pageSize: 20);

        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks(page);
        // The user lookup returns a real User with the sender's Id.
        var sender = User.CreateStaff("EMP-001", "Admin Smith");
        // Use reflection to force the User's Id to match the test's
        // expected sender — User's Id is set by the BaseEntity ctor
        // to a random Guid, but we need it to match TestValues.CreatedByUserId
        // for the dictionary projection to find it.
        typeof(BaseEntity).GetProperty("Id")!.SetValue(sender, TestValues.CreatedByUserId);
        userRepo.GetByIdsReadOnlyAsync(default!, default)
            .ReturnsForAnyArgs(new List<User> { sender });

        // Act
        var result = await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, broadcastRepo, userRepo, groupRepo, logger,
            CancellationToken.None);

        // Assert
        var dto = result.Items.Should().ContainSingle().Subject;
        dto.SentByUserId.Should().Be(TestValues.CreatedByUserId);
        dto.SentByUserName.Should().Be("Admin Smith");
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToBroadcastRepository()
    {
        // Arrange
        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, broadcastRepo, userRepo, groupRepo, logger, ct);

        // Assert
        await broadcastRepo.Received(1).GetPaginatedAsync(
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToUserRepository()
    {
        // Arrange
        // Use a system-emitted broadcast so the user repo lookup IS
        // called — wait, the opposite. System-emitted broadcasts skip
        // the user lookup. We need an admin-authored broadcast to
        // verify the user repo receives the cancellation token.
        var adminBroadcast = BroadcastNotification.Create(
            sentByUserId: TestValues.CreatedByUserId,
            scope: BroadcastScope.All,
            targetRoleName: null,
            targetGroupId: null,
            targetUserId: null,
            title: "Welcome",
            message: "Hello",
            fanoutKind: NotificationKind.Broadcast,
            recipientCount: 5);
        var page = new PaginatedResult<BroadcastNotification>(
            new[] { adminBroadcast }, totalCount: 1, pageNumber: 1, pageSize: 20);
        var (currentUser, broadcastRepo, userRepo, groupRepo, logger) = BuildMocks(page);

        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await GetBroadcastNotificationsQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, broadcastRepo, userRepo, groupRepo, logger, ct);

        // Assert
        await userRepo.Received(1).GetByIdsReadOnlyAsync(
            Arg.Any<IEnumerable<Guid>>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

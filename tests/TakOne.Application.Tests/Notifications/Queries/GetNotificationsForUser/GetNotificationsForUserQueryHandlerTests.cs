using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.DTOs;
using TakOne.Application.Notifications.Queries.GetNotificationsForUser;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Queries.GetNotificationsForUser;

/// <summary>
/// Unit tests for <see cref="GetNotificationsForUserQueryHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// query, the current-user service, the notification repository, a
/// logger, and a cancellation token. It returns a bare
/// <see cref="PaginatedResult{NotificationDto}"/> (NOT wrapped in
/// <c>Result&lt;&gt;</c>) — auth failure returns an EMPTY page (warning
/// logged), NOT a Result.Failure. This matches the existing
/// GetSalesPaginatedQuery contract.
///
/// COVERAGE TARGETS:
///   1. Auth failure → empty page (Items empty, TotalCount=0).
///   2. pageNumber &lt; 1 → clamp to 1.
///   3. pageSize &lt; 1 → clamp to 20.
///   4. pageSize > MaxPageSize(=100) → clamp to 100.
///   5. UnreadOnly flag forwarded verbatim to the repository.
///   6. Projection shape: each NotificationDto field is populated from
///      the corresponding Notification aggregate field.
///   7. page.TotalCount forwarded verbatim into the result's TotalCount.
///   8. page.Items projected 1:1 into the result's Items.
///   9. Unread detection via DTO's IsUnread computed property (null
///      ReadAtUtc → unread).
///  10. Auth failure logs warning (silent return, not Result.Failure).
///  11. Cancellation token forwarded to the repository.
/// </summary>
public class GetNotificationsForUserQueryHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // The handler's MaxPageSize constant is 100 — we use this literal
    // throughout the tests so the assertion is readable AND locks in the
    // SUT's value (a refactor that changes MaxPageSize without updating
    // tests would fail these tests).
    private const int ExpectedMaxPageSize = 100;

    private static GetNotificationsForUserQuery BuildValidQuery(
        int? pageNumber = null,
        int? pageSize = null,
        bool? unreadOnly = null)
        => new()
        {
            PageNumber = pageNumber ?? 1,
            PageSize = pageSize ?? 20,
            UnreadOnly = unreadOnly ?? false
        };

    // Builds a fully-wired NSubstitute environment:
    //   - currentUser authenticated as TestValues.CreatedByUserId
    //   - notificationRepository.GetPaginatedForUserAsync returns a page
    //     with 1 real Notification (built via Notification.Create) so
    //     we can observe the projection shape. TotalCount=1.
    private static (
        ICurrentUserService currentUser,
        INotificationRepository notificationRepo,
        ILogger<GetNotificationsForUserQueryHandler> logger,
        Notification notification)
        BuildMocks(Notification? notification = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);

        // Use a REAL Notification so the projection can read actual
        // property values (not mock-substitute returns).
        var actualNotification = notification
            ?? Notification.Create(
                userId: TestValues.CreatedByUserId,
                kind: NotificationKind.SaleSubmitted,
                saleId: TestValues.SaleId,
                saleDisplayNumber: "INT-1505-00000042",
                actorName: "Approver Name",
                reason: null);

        var page = new PaginatedResult<Notification>(
            new[] { actualNotification }, totalCount: 1, pageNumber: 1, pageSize: 20);

        var notificationRepo = Substitute.For<INotificationRepository>();
        notificationRepo.GetPaginatedForUserAsync(default, default, default, default, default)
            .ReturnsForAnyArgs(page);

        var logger = Substitute.For<ILogger<GetNotificationsForUserQueryHandler>>();

        return (currentUser, notificationRepo, logger, actualNotification);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAuthenticated_ReturnsPageFromRepository()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        var result = await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().HaveCount(1);
    }

    // ── Auth failure → empty page (NOT Result.Failure) ───────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsEmptyPage()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        // The handler returns a bare PaginatedResult<NotificationDto>
        // (NOT Result<PaginatedResult<...>>). Auth failure surfaces as
        // an empty page — same pattern as GetSalesPaginatedQuery.
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
        // Repo is NOT called on the auth-fail path.
        await notificationRepo.DidNotReceive().GetPaginatedForUserAsync(
            Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<bool>(), Arg.Any<NotificationKind?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsEmptyPage()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
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

    // ── Page clamping ───────────────────────────────────────────────────

    // pageNumber < 1 → clamp to 1 (the handler explicitly clamps so a
    // malicious or buggy client can't request negative pages).
    [Fact]
    public async Task HandleAsync_WhenPageNumberIsZero_ClampsToOne()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(pageNumber: 0), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Is<int>(p => p == 1),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Any<NotificationKind?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenPageNumberIsNegative_ClampsToOne()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(pageNumber: -5), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Is<int>(p => p == 1),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Any<NotificationKind?>(),
            Arg.Any<CancellationToken>());
    }

    // pageSize < 1 → clamp to 20 (the default page size).
    [Fact]
    public async Task HandleAsync_WhenPageSizeIsZero_ClampsToTwenty()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(pageSize: 0), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Is<int>(p => p == 20),
            Arg.Any<bool>(),
            Arg.Any<NotificationKind?>(),
            Arg.Any<CancellationToken>());
    }

    // pageSize > MaxPageSize(=100) → clamp to 100.
    [Fact]
    public async Task HandleAsync_WhenPageSizeExceedsMax_ClampsToHundred()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(pageSize: 500), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Is<int>(p => p == ExpectedMaxPageSize),
            Arg.Any<bool>(),
            Arg.Any<NotificationKind?>(),
            Arg.Any<CancellationToken>());
    }

    // pageSize == MaxPageSize(=100) → no clamp (passes through unchanged).
    [Fact]
    public async Task HandleAsync_WhenPageSizeIsExactlyMax_PassesThroughUnchanged()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(pageSize: ExpectedMaxPageSize), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Is<int>(p => p == ExpectedMaxPageSize),
            Arg.Any<bool>(),
            Arg.Any<NotificationKind?>(),
            Arg.Any<CancellationToken>());
    }

    // ── UnreadOnly flag forwarding ───────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenUnreadOnlyIsTrue_ForwardsItToRepository()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(unreadOnly: true), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Is<bool>(u => u),
            Arg.Any<NotificationKind?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUnreadOnlyIsFalse_ForwardsItToRepository()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(unreadOnly: false), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Is<bool>(u => !u),
            Arg.Any<NotificationKind?>(),
            Arg.Any<CancellationToken>());
    }

    // ── Projection shape ────────────────────────────────────────────────

    // The handler projects each Notification aggregate to a NotificationDto
    // 1:1. We verify every field on the DTO is populated from the source
    // aggregate's corresponding property. Locking in the projection
    // shape protects the UI's render contract from silent refactors.
    [Fact]
    public async Task HandleAsync_WhenNotificationExists_ProjectsAllFieldsToDto()
    {
        // Arrange
        // Build a Notification with KNOWN values so the assertions are
        // deterministic.
        var notification = Notification.Create(
            userId: TestValues.CreatedByUserId,
            kind: NotificationKind.SaleApproved,
            saleId: TestValues.SaleId,
            saleDisplayNumber: "INT-1505-00000099",
            actorName: "John Doe",
            reason: null);
        var (currentUser, notificationRepo, logger, _) = BuildMocks(notification);

        // Act
        var result = await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        var dto = result.Items.Should().ContainSingle().Subject;
        dto.Id.Should().Be(notification.Id);
        dto.Kind.Should().Be(NotificationKind.SaleApproved);
        dto.SaleId.Should().Be(TestValues.SaleId);
        dto.SaleDisplayNumber.Should().Be("INT-1505-00000099");
        dto.ActorName.Should().Be("John Doe");
        dto.Reason.Should().BeNull();
        dto.Title.Should().BeNull();
        dto.Message.Should().BeNull();
        dto.BroadcastId.Should().BeNull();
        dto.CreatedAtUtc.Should().Be(notification.CreatedAtUtc);
        dto.ReadAtUtc.Should().Be(notification.ReadAtUtc);
    }

    // ── page.TotalCount forwarding ──────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenRepositoryReturnsPageWithTotalCount_ForwardsTotalCountToResult()
    {
        // Arrange
        // Override the default mock to return a page with 3 items +
        // TotalCount=42 (the count of ALL the user's notifications, not
        // just this page's slice — the UI uses this to render the
        // pagination control's "1 / 5 of 42" indicator).
        var (currentUser, notificationRepo, logger, _) = BuildMocks();
        var page = new PaginatedResult<Notification>(
            Array.Empty<Notification>(), totalCount: 42, pageNumber: 1, pageSize: 20);
        notificationRepo.GetPaginatedForUserAsync(default, default, default, default, default)
            .ReturnsForAnyArgs(page);

        // Act
        var result = await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        result.TotalCount.Should().Be(42);
        result.Items.Should().BeEmpty();
    }

    // ── page.Items projection ──────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenRepositoryReturnsMultipleItems_ProjectsAllToDtos()
    {
        // Arrange
        // Build 2 distinct Notifications so we can verify the projection
        // preserves count + identity. The repository returns a page
        // containing both.
        var n1 = Notification.Create(
            userId: TestValues.CreatedByUserId,
            kind: NotificationKind.SaleSubmitted,
            saleId: TestValues.SaleId,
            saleDisplayNumber: "INT-1505-00000001",
            actorName: "Actor One",
            reason: null);
        var n2 = Notification.Create(
            userId: TestValues.CreatedByUserId,
            kind: NotificationKind.SaleCancelled,
            saleId: TestValues.SaleId,
            saleDisplayNumber: "INT-1505-00000002",
            actorName: "Actor Two",
            reason: "Out of stock");
        var page = new PaginatedResult<Notification>(
            new[] { n1, n2 }, totalCount: 2, pageNumber: 1, pageSize: 20);

        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);
        var notificationRepo = Substitute.For<INotificationRepository>();
        notificationRepo.GetPaginatedForUserAsync(default, default, default, default, default)
            .ReturnsForAnyArgs(page);
        var logger = Substitute.For<ILogger<GetNotificationsForUserQueryHandler>>();

        // Act
        var result = await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger,
            CancellationToken.None);

        // Assert
        result.Items.Should().HaveCount(2);
        // Verify identity preservation + that the projection didn't
        // reorder or merge items.
        result.Items.Should().Contain(d => d.Id == n1.Id);
        result.Items.Should().Contain(d => d.Id == n2.Id);
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToRepository()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, notificationRepo, logger, ct);

        // Assert
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Any<NotificationKind?>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    // ── Round 4: per-kind filter pass-through ──────────────────────────

    [Fact]
    public async Task HandleAsync_WithKind_ForwardsItToRepository()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            new GetNotificationsForUserQuery { Kind = NotificationKind.Broadcast },
            currentUser, notificationRepo, logger, CancellationToken.None);

        // Assert
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Is<NotificationKind?>(k => k == NotificationKind.Broadcast),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WithoutKind_ForwardsNull()
    {
        // Arrange
        var (currentUser, notificationRepo, logger, _) = BuildMocks();

        // Act
        await GetNotificationsForUserQueryHandler.HandleAsync(
            new GetNotificationsForUserQuery(),
            currentUser, notificationRepo, logger, CancellationToken.None);

        // Assert — null Kind means "all kinds" (the repo adds no clause).
        await notificationRepo.Received(1).GetPaginatedForUserAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<bool>(),
            Arg.Is<NotificationKind?>(k => k == null),
            Arg.Any<CancellationToken>());
    }
}

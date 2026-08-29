using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Commands.SetNotificationMuted;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Commands.SetNotificationMuted;

/// <summary>
/// Unit tests for <see cref="SetNotificationMutedCommandHandler"/>.
///
/// COVERAGE APPROACH: NSubstitute mocks. The handler is an UPSERT:
///   • row exists + mute → Mute() (tracked entity, no INSERT)
///   • row exists + unmute → Unmute()
///   • no row + mute → Create + AddAsync (sparse storage: first mute)
///   • no row + unmute → no write, Success (already the default)
/// plus the auth guard, the undefined-kind guard, and SaveChanges timing.
/// </summary>
public class SetNotificationMutedCommandHandlerTests
{
    private static (
        ICurrentUserService currentUser,
        INotificationPreferenceRepository preferenceRepo,
        IUnitOfWork unitOfWork,
        ILogger<SetNotificationMutedCommandHandler> logger)
        BuildMocks(
            bool authenticated = true,
            NotificationPreference? existing = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(authenticated);
        currentUser.UserId.Returns(TestValues.UserId);

        var preferenceRepo = Substitute.For<INotificationPreferenceRepository>();
        preferenceRepo.GetForUserAsync(
                Arg.Any<Guid>(), Arg.Any<NotificationKind>(), Arg.Any<CancellationToken>())
            .Returns(existing);

        var unitOfWork = Substitute.For<IUnitOfWork>();

        var logger = Substitute.For<ILogger<SetNotificationMutedCommandHandler>>();

        return (currentUser, preferenceRepo, unitOfWork, logger);
    }

    // ── First mute (no row) ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_FirstMute_CreatesMutedRowForCurrentUser()
    {
        // Arrange — sparse storage: the FIRST mute creates the row.
        var (currentUser, preferenceRepo, unitOfWork, logger) = BuildMocks();
        var command = new SetNotificationMutedCommand(
            Kind: NotificationKind.SaleSubmitted, IsMuted: true);

        // Act
        var result = await SetNotificationMutedCommandHandler.HandleAsync(
            command, currentUser, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert — a muted row is created FOR THE CURRENT USER (scope
        // guard: the command carries no user Id; the handler resolves it).
        result.IsSuccess.Should().BeTrue();
        await preferenceRepo.Received(1).AddAsync(
            Arg.Is<NotificationPreference>(p =>
                p.UserId == TestValues.UserId
                && p.Kind == NotificationKind.SaleSubmitted
                && p.IsMuted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_FirstMute_PersistsViaSaveChanges()
    {
        // The Wolverine transactional-middleware contract: the handler
        // flushes explicitly (see MarkNotificationAsReadCommandHandler's
        // identical IUnitOfWork rationale).
        var (currentUser, preferenceRepo, unitOfWork, logger) = BuildMocks();

        await SetNotificationMutedCommandHandler.HandleAsync(
            new SetNotificationMutedCommand(NotificationKind.SaleInvoiced, IsMuted: true),
            currentUser, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── No-op unmute (no row) ───────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NoRowUnmute_ReturnsSuccessWithoutWrite()
    {
        // Arrange — un-muting a never-muted kind is ALREADY the default
        // state; the handler must not create a row for it (sparse storage
        // stays sparse) and must not call SaveChanges.
        var (currentUser, preferenceRepo, unitOfWork, logger) = BuildMocks();
        var command = new SetNotificationMutedCommand(
            Kind: NotificationKind.SaleSubmitted, IsMuted: false);

        // Act
        var result = await SetNotificationMutedCommandHandler.HandleAsync(
            command, currentUser, preferenceRepo, unitOfWork, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await preferenceRepo.DidNotReceiveWithAnyArgs().AddAsync(
            default(NotificationPreference)!, default);
        await unitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    // ── Toggle existing row ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_MuteExistingRow_DoesNotInsertSecondRow()
    {
        // Arrange — the row already exists (previously un-muted).
        var existing = NotificationPreference.Create(
            TestValues.UserId, NotificationKind.Broadcast, isMuted: false);
        var (currentUser, preferenceRepo, unitOfWork, logger) = BuildMocks(existing: existing);

        // Act
        var result = await SetNotificationMutedCommandHandler.HandleAsync(
            new SetNotificationMutedCommand(NotificationKind.Broadcast, IsMuted: true),
            currentUser, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        // Assert — the TRACKED entity is mutated in place; no INSERT.
        result.IsSuccess.Should().BeTrue();
        existing.IsMuted.Should().BeTrue();
        await preferenceRepo.DidNotReceiveWithAnyArgs().AddAsync(
            default(NotificationPreference)!, default);
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_UnmuteExistingRow_MutatesTrackedEntity()
    {
        // Arrange — the row exists (currently muted).
        var existing = NotificationPreference.Create(
            TestValues.UserId, NotificationKind.AppUpdate, isMuted: true);
        var (currentUser, preferenceRepo, unitOfWork, logger) = BuildMocks(existing: existing);

        // Act
        var result = await SetNotificationMutedCommandHandler.HandleAsync(
            new SetNotificationMutedCommand(NotificationKind.AppUpdate, IsMuted: false),
            currentUser, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        // Assert — Unmute() in place; the row is NOT deleted (durable
        // explicit choice, cheap re-toggle).
        result.IsSuccess.Should().BeTrue();
        existing.IsMuted.Should().BeFalse();
        await preferenceRepo.DidNotReceiveWithAnyArgs().AddAsync(
            default(NotificationPreference)!, default);
    }

    // ── Guards ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenUnauthenticated_ReturnsAuthRequiredError()
    {
        var (currentUser, preferenceRepo, unitOfWork, logger) = BuildMocks(authenticated: false);

        var result = await SetNotificationMutedCommandHandler.HandleAsync(
            new SetNotificationMutedCommand(NotificationKind.Broadcast, IsMuted: true),
            currentUser, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NotificationAuthRequired");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(-1)]
    public async Task HandleAsync_WithUndefinedKind_ReturnsInvalidKindError(int rawKind)
    {
        // A bad model bind producing (NotificationKind)42 must surface a
        // clean, localizable Result — NOT a 500 from the domain factory's
        // DomainException.
        var (currentUser, preferenceRepo, unitOfWork, logger) = BuildMocks();

        var result = await SetNotificationMutedCommandHandler.HandleAsync(
            new SetNotificationMutedCommand((NotificationKind)rawKind, IsMuted: true),
            currentUser, preferenceRepo, unitOfWork, logger, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NotificationKindInvalid");
    }
}

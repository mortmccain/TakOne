using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.Queries.GetNotificationPreferences;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Notifications.Queries.GetNotificationPreferences;

/// <summary>
/// Unit tests for <see cref="GetNotificationPreferencesQueryHandler"/>.
///
/// COVERAGE APPROACH: NSubstitute mocks for ICurrentUserService +
/// INotificationPreferenceRepository + logger. The handler merges the
/// user's sparse persisted rows against the FULL NotificationKind enum —
/// tests cover: all-defaults (no rows), partial mutes, enum-completeness
/// + ordering, the auth guard, and CT forwarding.
/// </summary>
public class GetNotificationPreferencesQueryHandlerTests
{
    private static (
        ICurrentUserService currentUser,
        INotificationPreferenceRepository preferenceRepo,
        ILogger<GetNotificationPreferencesQueryHandler> logger)
        BuildMocks(
            bool authenticated = true,
            IReadOnlyList<NotificationPreference>? persisted = null)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(authenticated);
        currentUser.UserId.Returns(TestValues.UserId);

        var preferenceRepo = Substitute.For<INotificationPreferenceRepository>();
        preferenceRepo.GetAllForUserAsync(
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(persisted ?? Array.Empty<NotificationPreference>());

        var logger = Substitute.For<ILogger<GetNotificationPreferencesQueryHandler>>();

        return (currentUser, preferenceRepo, logger);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAuthenticated_ReturnsSuccess()
    {
        // Arrange
        var (currentUser, preferenceRepo, logger) = BuildMocks();

        // Act
        var result = await GetNotificationPreferencesQueryHandler.HandleAsync(
            new GetNotificationPreferencesQuery(),
            currentUser, preferenceRepo, logger, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WithNoPersistedRows_ReturnsAllKindsUnmuted()
    {
        // Arrange — sparse storage: a user who never muted anything has
        // ZERO preference rows. The settings UI still needs the FULL kind
        // list (all toggled off) to render.
        var (currentUser, preferenceRepo, logger) = BuildMocks(persisted: Array.Empty<NotificationPreference>());

        // Act
        var result = await GetNotificationPreferencesQueryHandler.HandleAsync(
            new GetNotificationPreferencesQuery(),
            currentUser, preferenceRepo, logger, CancellationToken.None);

        // Assert — one entry per enum value, all un-muted, enum-ordered.
        result.Value.Should().HaveCount(Enum.GetValues<NotificationKind>().Length);
        result.Value.Should().OnlyContain(p => !p.IsMuted);
        result.Value.Select(p => p.Kind).Should().ContainInOrder(
            Enum.GetValues<NotificationKind>().OrderBy(k => (int)k));
    }

    [Fact]
    public async Task HandleAsync_WithMutedKinds_ReflectsPersistedMuteFlags()
    {
        // Arrange — the user muted SaleCancelled + Broadcast. Both must
        // come back muted; everything else un-muted. A persisted
        // IsMuted=false row (was muted, then un-muted) must read as false.
        var persisted = new[]
        {
            NotificationPreference.Create(TestValues.UserId, NotificationKind.SaleCancelled, isMuted: true),
            NotificationPreference.Create(TestValues.UserId, NotificationKind.Broadcast, isMuted: true),
            NotificationPreference.Create(TestValues.UserId, NotificationKind.AppUpdate, isMuted: false)
        };
        var (currentUser, preferenceRepo, logger) = BuildMocks(persisted: persisted);

        // Act
        var result = await GetNotificationPreferencesQueryHandler.HandleAsync(
            new GetNotificationPreferencesQuery(),
            currentUser, preferenceRepo, logger, CancellationToken.None);

        // Assert
        var byKind = result.Value.ToDictionary(p => p.Kind, p => p.IsMuted);
        byKind[NotificationKind.SaleCancelled].Should().BeTrue();
        byKind[NotificationKind.Broadcast].Should().BeTrue();
        byKind[NotificationKind.AppUpdate].Should().BeFalse("an explicit un-muted row must read as not muted");
        byKind[NotificationKind.SaleSubmitted].Should().BeFalse("no row = sparse default = not muted");
        byKind[NotificationKind.GroupChanged].Should().BeFalse();
    }

    // The settings UI renders the list straight from the query result —
    // a FUTURE enum value must appear automatically without a code change
    // (the handler merges against Enum.GetValues at runtime).
    [Fact]
    public async Task HandleAsync_ResultAlwaysCoversEveryDefinedKind()
    {
        var (currentUser, preferenceRepo, logger) = BuildMocks(persisted: Array.Empty<NotificationPreference>());

        var result = await GetNotificationPreferencesQueryHandler.HandleAsync(
            new GetNotificationPreferencesQuery(),
            currentUser, preferenceRepo, logger, CancellationToken.None);

        result.Value.Select(p => p.Kind)
            .Should().BeEquivalentTo(Enum.GetValues<NotificationKind>());
    }

    // ── Auth guard ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenUnauthenticated_ReturnsAuthRequiredError()
    {
        // Arrange — defense-in-depth: the [RequireAuthentication] attribute
        // should already have rejected the call at the Wolverine boundary.
        var (currentUser, preferenceRepo, logger) = BuildMocks(authenticated: false);

        // Act
        var result = await GetNotificationPreferencesQueryHandler.HandleAsync(
            new GetNotificationPreferencesQuery(),
            currentUser, preferenceRepo, logger, CancellationToken.None);

        // Assert — stable, culture-neutral error code (UI-localizable).
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("NotificationAuthRequired");
    }

    // ── Scope + CT forwarding ───────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_LoadsPreferencesForCurrentUserOnly()
    {
        // Anti-CSRF scope guard: the repository must be called with the
        // CURRENT user's Id (resolved from ICurrentUserService), never an
        // Id supplied by the caller.
        var (currentUser, preferenceRepo, logger) = BuildMocks();

        await GetNotificationPreferencesQueryHandler.HandleAsync(
            new GetNotificationPreferencesQuery(),
            currentUser, preferenceRepo, logger, CancellationToken.None);

        await preferenceRepo.Received(1).GetAllForUserAsync(
            TestValues.UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ForwardsCancellationToken()
    {
        var (currentUser, preferenceRepo, logger) = BuildMocks();
        using var cts = new CancellationTokenSource();

        await GetNotificationPreferencesQueryHandler.HandleAsync(
            new GetNotificationPreferencesQuery(),
            currentUser, preferenceRepo, logger, cts.Token);

        await preferenceRepo.Received(1).GetAllForUserAsync(
            Arg.Any<Guid>(), cts.Token);
    }
}

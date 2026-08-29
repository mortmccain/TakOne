using FluentAssertions;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;
using TakOne.Testing;
using Xunit;

namespace TakOne.Domain.Tests.Notifications;

/// <summary>
/// Unit tests for the <see cref="NotificationPreference"/> aggregate root
/// (Round 3 — per-user, per-kind mute flags).
///
/// Verifies the <see cref="NotificationPreference.Create"/> factory (initial
/// state + guards) and the idempotent <see cref="NotificationPreference.Mute"/>/
/// <see cref="NotificationPreference.Unmute"/> transitions (no timestamp churn
/// on no-op toggles — the UpdatedAtUtc field is the "when did this change"
/// diagnostic, so a spurious bump on a no-op call would corrupt it).
/// </summary>
public class NotificationPreferenceTests
{
    // ======================================================================
    //                          Create — happy path
    // ======================================================================

    [Fact]
    public void Create_WithMutedTrue_ReturnsMutedPreference()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act — the upsert command's "first mute" branch.
        var preference = NotificationPreference.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.SaleApproved,
            isMuted: true);

        // Assert
        preference.Id.Should().NotBeEmpty();
        preference.UserId.Should().Be(TestValues.UserId);
        preference.Kind.Should().Be(NotificationKind.SaleApproved);
        preference.IsMuted.Should().BeTrue();
        preference.UpdatedAtUtc.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_WithMutedFalse_ReturnsUnmutedPreference()
    {
        // Act — explicit un-muted row (was muted before; the row is kept,
        // never deleted — see the aggregate's sparse-storage remarks).
        var preference = NotificationPreference.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.Broadcast,
            isMuted: false);

        // Assert
        preference.IsMuted.Should().BeFalse();
    }

    [Fact]
    public void Create_DoesNotRaiseDomainEvents()
    {
        // A preference flip is a single-user settings mutation with no
        // downstream fanout — the aggregate contract says NO domain events
        // (nothing to broadcast; the settings UI reflects the change
        // optimistically). Locking this in prevents someone "helpfully"
        // adding an event later that would fan out through Wolverine's
        // outbox on every toggle.
        var preference = NotificationPreference.Create(
            userId: TestValues.UserId,
            kind: NotificationKind.AppUpdate,
            isMuted: true);

        preference.DomainEvents.Should().BeEmpty();
    }

    // ======================================================================
    //                          Create — guards
    // ======================================================================

    [Fact]
    public void Create_WithEmptyUserId_ThrowsDomainException()
    {
        // Act
        var act = () => NotificationPreference.Create(
            userId: Guid.Empty,
            kind: NotificationKind.SaleSubmitted,
            isMuted: true);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("*non-empty user Id*");
    }

    [Theory]
    [InlineData(0)]   // below the first defined value
    [InlineData(42)]  // far above the last defined value
    [InlineData(-1)]  // negative
    public void Create_WithUndefinedKind_ThrowsDomainException(int rawKind)
    {
        // Act — a bad model bind can produce an undefined enum value; the
        // factory must fail loudly (not silently persist a row that breaks
        // the unique index semantics + the settings UI's kind rendering).
        var act = () => NotificationPreference.Create(
            userId: TestValues.UserId,
            kind: (NotificationKind)rawKind,
            isMuted: true);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("*not a valid NotificationKind*");
    }

    // ======================================================================
    //                          Mute / Unmute transitions
    // ======================================================================

    [Fact]
    public void Mute_OnUnmutedPreference_SetsIsMutedAndBumpsTimestamp()
    {
        // Arrange
        var preference = NotificationPreference.Create(
            TestValues.UserId, NotificationKind.SaleInvoiced, isMuted: false);
        var before = preference.UpdatedAtUtc;

        // Act
        preference.Mute();

        // Assert
        preference.IsMuted.Should().BeTrue();
        preference.UpdatedAtUtc.Should().BeAfter(before);
    }

    [Fact]
    public void Mute_OnAlreadyMutedPreference_IsIdempotentNoOp()
    {
        // Arrange
        var preference = NotificationPreference.Create(
            TestValues.UserId, NotificationKind.SaleInvoiced, isMuted: true);
        var timestamp = preference.UpdatedAtUtc;

        // Act — double-click / double-fire path.
        preference.Mute();

        // Assert — no spurious UPDATE, no timestamp churn (the timestamp is
        // the "when did the user's choice change" diagnostic).
        preference.IsMuted.Should().BeTrue();
        preference.UpdatedAtUtc.Should().Be(timestamp);
    }

    [Fact]
    public void Unmute_OnMutedPreference_ClearsIsMutedAndBumpsTimestamp()
    {
        // Arrange
        var preference = NotificationPreference.Create(
            TestValues.UserId, NotificationKind.GroupChanged, isMuted: true);
        var before = preference.UpdatedAtUtc;

        // Act
        preference.Unmute();

        // Assert
        preference.IsMuted.Should().BeFalse();
        preference.UpdatedAtUtc.Should().BeAfter(before);
    }

    [Fact]
    public void Unmute_OnAlreadyUnmutedPreference_IsIdempotentNoOp()
    {
        // Arrange
        var preference = NotificationPreference.Create(
            TestValues.UserId, NotificationKind.GroupChanged, isMuted: false);
        var timestamp = preference.UpdatedAtUtc;

        // Act — "unmute" on a never-muted row (sparse default).
        preference.Unmute();

        // Assert
        preference.IsMuted.Should().BeFalse();
        preference.UpdatedAtUtc.Should().Be(timestamp);
    }
}

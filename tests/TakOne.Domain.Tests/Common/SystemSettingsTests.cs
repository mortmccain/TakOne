using FluentAssertions;
using TakOne.Domain.Common.Entities;
using TakOne.Domain.Common.Enums;
using TakOne.SharedKernel.Common;
using Xunit;

namespace TakOne.Domain.Tests.Common;

/// <summary>
/// Unit tests for the <see cref="SystemSettings"/> singleton aggregate.
/// Verifies <see cref="SystemSettings.CreateDefault"/> defaults,
/// <see cref="SystemSettings.Load"/> with persisted state,
/// <see cref="SystemSettings.UpdateLimitMode"/> (mode-change + same-value
/// no-op + undefined-enum guards), and
/// <see cref="SystemSettings.UpdateLastKnownAppVersion"/> (first-write,
/// same-value no-op, empty/whitespace no-op).
/// </summary>
public class SystemSettingsTests
{
    // ======================================================================
    //                          CreateDefault
    // ======================================================================

    [Fact]
    public void CreateDefault_ReturnsSettingsWithCountOnlyModeAndNullAppVersion()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act
        var settings = SystemSettings.CreateDefault();

        // Assert — defaults preserve the pre-salary-feature behaviour
        settings.Id.Should().Be(SystemSettings.SingletonId);
        settings.LimitMode.Should().Be(LimitMode.CountOnly);
        settings.LastKnownAppVersion.Should().BeNull();
        settings.UpdatedAt.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
    }

    // ======================================================================
    //                          Load
    // ======================================================================

    [Fact]
    public void Load_SetsAllFieldsIncludingLastKnownAppVersion()
    {
        // Arrange — persisted state from a DB row
        var updatedAt = new DateTime(2025, 3, 15, 10, 0, 0, DateTimeKind.Utc);

        // Act
        var settings = SystemSettings.Load(LimitMode.Both, updatedAt, "1.2.3");

        // Assert
        settings.Id.Should().Be(SystemSettings.SingletonId);
        settings.LimitMode.Should().Be(LimitMode.Both);
        settings.UpdatedAt.Should().Be(updatedAt);
        settings.LastKnownAppVersion.Should().Be("1.2.3");
    }

    // ======================================================================
    //                          SingletonId
    // ======================================================================

    [Fact]
    public void SingletonId_IsGuidEmpty()
    {
        // Assert — the singleton row's PK is fixed at Guid.Empty
        SystemSettings.SingletonId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void CreateDefault_IdIsGuidEmpty()
    {
        // Act
        var settings = SystemSettings.CreateDefault();

        // Assert
        settings.Id.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Load_IdIsGuidEmpty()
    {
        // Act
        var settings = SystemSettings.Load(LimitMode.CountOnly, DateTime.UtcNow, null);

        // Assert
        settings.Id.Should().Be(Guid.Empty);
    }

    // ======================================================================
    //                          UpdateLimitMode
    // ======================================================================

    [Fact]
    public void UpdateLimitMode_WhenModeChanges_ChangesModeAndBumpsUpdatedAt()
    {
        // Arrange
        var settings = SystemSettings.CreateDefault(); // starts at CountOnly
        var originalUpdatedAt = settings.UpdatedAt;

        // Act
        settings.UpdateLimitMode(LimitMode.Both);

        // Assert
        settings.LimitMode.Should().Be(LimitMode.Both);
        settings.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public void UpdateLimitMode_WhenModeSame_DoesNotBumpUpdatedAt()
    {
        // Arrange — start at CountOnly; pass CountOnly again → no-op
        var settings = SystemSettings.CreateDefault();
        var originalUpdatedAt = settings.UpdatedAt;

        // Act
        settings.UpdateLimitMode(LimitMode.CountOnly);

        // Assert
        settings.LimitMode.Should().Be(LimitMode.CountOnly);
        settings.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    [Fact]
    public void UpdateLimitMode_WithUninitializedEnumValue_ThrowsMustBeOneOfMessage()
    {
        // Arrange — default(LimitMode) = 0 is invalid (the enum starts at 1).
        // Implementation note: the SUT's EnsureLimitModeValid has TWO guards:
        //   1) Enum.IsDefined check → "LimitMode must be one of: ..."
        //   2) explicit zero-check → "LimitMode cannot be 0 (Uninitialized). ..."
        // Guard #1 fires first for mode=0 because LimitMode starts at 1
        // (so Enum.IsDefined(typeof(LimitMode), 0) returns false), making
        // guard #2 effectively unreachable dead-code. We assert the actual
        // SUT behavior: the "must be one of" message is what's thrown.
        var settings = SystemSettings.CreateDefault();

        // Act
        Action act = () => settings.UpdateLimitMode((LimitMode)0);

        // Assert — actual behavior is the first guard firing
        act.Should().Throw<DomainException>()
            .WithMessage("LimitMode must be one of: CountOnly, SalaryOnly, Both.");
    }

    [Fact]
    public void UpdateLimitMode_WithUndefinedEnumValue_Throws()
    {
        // Arrange — (LimitMode)42 is not a defined enum member
        var settings = SystemSettings.CreateDefault();

        // Act
        Action act = () => settings.UpdateLimitMode((LimitMode)42);

        // Assert — Enum.IsDefined catches the undefined value first
        act.Should().Throw<DomainException>()
            .WithMessage("LimitMode must be one of: CountOnly, SalaryOnly, Both.");
    }

    // ======================================================================
    //                          UpdateLastKnownAppVersion
    // ======================================================================

    [Fact]
    public void UpdateLastKnownAppVersion_WhenVersionNew_SetsVersionAndBumpsUpdatedAt()
    {
        // Arrange — fresh install: LastKnownAppVersion starts null
        var settings = SystemSettings.CreateDefault();
        var originalUpdatedAt = settings.UpdatedAt;

        // Act
        settings.UpdateLastKnownAppVersion("1.0.0");

        // Assert
        settings.LastKnownAppVersion.Should().Be("1.0.0");
        settings.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public void UpdateLastKnownAppVersion_WhenVersionSame_DoesNotBumpUpdatedAt()
    {
        // Arrange — start with version "1.0.0"; pass same → no-op
        var settings = SystemSettings.Load(LimitMode.CountOnly, DateTime.UtcNow, "1.0.0");
        var originalUpdatedAt = settings.UpdatedAt;

        // Act
        settings.UpdateLastKnownAppVersion("1.0.0");

        // Assert — same version → no spurious DB UPDATE generated
        settings.LastKnownAppVersion.Should().Be("1.0.0");
        settings.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    [Fact]
    public void UpdateLastKnownAppVersion_WithEmptyVersion_IsSilentNoOp()
    {
        // Arrange — defensive: empty input doesn't crash the host
        var settings = SystemSettings.Load(LimitMode.CountOnly, DateTime.UtcNow, "1.0.0");
        var originalUpdatedAt = settings.UpdatedAt;
        var originalVersion = settings.LastKnownAppVersion;

        // Act
        settings.UpdateLastKnownAppVersion("");

        // Assert — no throw, no update to LastKnownAppVersion or UpdatedAt
        settings.LastKnownAppVersion.Should().Be(originalVersion);
        settings.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    [Fact]
    public void UpdateLastKnownAppVersion_WithWhitespaceVersion_IsSilentNoOp()
    {
        // Arrange — defensive: whitespace input also doesn't crash the host
        var settings = SystemSettings.Load(LimitMode.CountOnly, DateTime.UtcNow, "1.0.0");
        var originalUpdatedAt = settings.UpdatedAt;
        var originalVersion = settings.LastKnownAppVersion;

        // Act
        settings.UpdateLastKnownAppVersion("   ");

        // Assert
        settings.LastKnownAppVersion.Should().Be(originalVersion);
        settings.UpdatedAt.Should().Be(originalUpdatedAt);
    }
}

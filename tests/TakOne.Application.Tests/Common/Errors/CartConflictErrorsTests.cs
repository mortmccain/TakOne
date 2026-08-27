using FluentAssertions;
using TakOne.Application.Common.Errors;
using Xunit;

namespace TakOne.Application.Tests.Common.Errors;

/// <summary>
/// Unit tests for <see cref="CartConflictErrors"/> — the culture-neutral
/// "cart was modified by another session" stable-code catalog. This catalog
/// has NO payload — Format() returns just the prefix, and TryParse() does
/// nothing more than check that the error string starts with the prefix.
/// </summary>
public class CartConflictErrorsTests
{
    // ── Prefix contract ────────────────────────────────────────────────

    [Fact]
    public void Prefix_WhenRead_ReturnsCartConflictWithColon()
    {
        // Arrange

        // Act
        var prefix = CartConflictErrors.Prefix;

        // Assert
        prefix.Should().Be("CartConflict:");
    }

    // ── Format() ────────────────────────────────────────────────────────

    [Fact]
    public void Format_WhenCalled_ReturnsExactlyThePrefix()
    {
        // Arrange
        // This catalog carries no payload — the error string is the prefix.

        // Act
        var result = CartConflictErrors.Format();

        // Assert
        result.Should().Be(CartConflictErrors.Prefix);
        result.Should().Be("CartConflict:");
    }

    [Fact]
    public void Format_WhenCalledTwice_ReturnsSameValue()
    {
        // Arrange
        // Format() is pure — no randomness, no clock, no env — so two calls
        // must return the exact same string.

        // Act
        var first = CartConflictErrors.Format();
        var second = CartConflictErrors.Format();

        // Assert
        first.Should().Be(second);
    }

    // ── TryParse() — positive cases ─────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenExactPrefix_ReturnsTrue()
    {
        // Arrange
        // The bare prefix is the canonical Format() output — must parse.

        // Act
        var result = CartConflictErrors.TryParse("CartConflict:");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void TryParse_WhenGivenPrefixWithExtraContent_ReturnsTrue()
    {
        // Arrange
        // TryParse is a StartsWith check, so any error string beginning
        // with the prefix is recognized — even though Format() never
        // produces trailing content, defensive pages may surface extra
        // debug suffixes in dev environments.

        // Act
        var result = CartConflictErrors.TryParse("CartConflict:foo");

        // Assert
        result.Should().BeTrue();
    }

    // ── TryParse() — negative cases ────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenNull_ReturnsFalse()
    {
        // Arrange
        // The null/empty guard fires before the StartsWith check.

        // Act
        var result = CartConflictErrors.TryParse(null);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenEmptyString_ReturnsFalse()
    {
        // Arrange
        // The null/empty guard fires before the StartsWith check.

        // Act
        var result = CartConflictErrors.TryParse(string.Empty);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenWhitespaceOnly_ReturnsFalse()
    {
        // Arrange
        // string.IsNullOrEmpty("  ") returns FALSE — so the guard does not
        // fire. Then StartsWith(Prefix) returns false → overall false.
        // This is a subtle edge case worth pinning.

        // Act
        var result = CartConflictErrors.TryParse("  ");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenUnrelatedError_ReturnsFalse()
    {
        // Arrange
        // A different stable-code catalog's prefix must not be misparsed.

        // Act
        var result = CartConflictErrors.TryParse("OtherError:");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenPrefixWithDifferentCase_ReturnsFalse()
    {
        // Arrange
        // TryParse uses StringComparison.Ordinal — case-sensitive. A
        // lowercase 'c' must not match the uppercase 'C' in the prefix.

        // Act
        var result = CartConflictErrors.TryParse("cartconflict:");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenPrefixWithoutColon_ReturnsFalse()
    {
        // Arrange
        // The prefix is "CartConflict:" (with colon). A bare "CartConflict"
        // (no colon) does not StartsWith the prefix and must be rejected —
        // otherwise an unrelated error like "CartConflictsResolved" would
        // false-positive.

        // Act
        var result = CartConflictErrors.TryParse("CartConflict");

        // Assert
        result.Should().BeFalse();
    }

    // ── Round-trip Format → TryParse ─────────────────────────────────────

    [Fact]
    public void RoundTrip_FormatThenTryParse_ReturnsTrue()
    {
        // Arrange
        var formatted = CartConflictErrors.Format();

        // Act
        var parsed = CartConflictErrors.TryParse(formatted);

        // Assert
        parsed.Should().BeTrue();
    }
}

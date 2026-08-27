using FluentAssertions;
using TakOne.Application.Common.Errors;
using Xunit;

namespace TakOne.Application.Tests.Common.Errors;

/// <summary>
/// Unit tests for <see cref="NoCustomerGroupErrors"/> — the culture-neutral
/// "user has no customer group assigned" stable-code catalog.
///
/// IMPORTANT SUT DETAIL: this catalog uses EXACT STRING EQUALITY (==), NOT
/// StartsWith. So "NoCustomerGroup" parses as true but "NoCustomerGroupExtra"
/// or "NoCustomerGroup:" parses as false. This differs from the other
/// catalogs (CartConflict, PurchaseLimit, etc.) which use StartsWith. The
/// asymmetry is intentional: this catalog carries no payload, so a prefix
/// match would false-positive on unrelated error strings that happen to
/// start with "NoCustomerGroup".
/// </summary>
public class NoCustomerGroupErrorsTests
{
    // ── Prefix contract ────────────────────────────────────────────────

    [Fact]
    public void Prefix_WhenRead_ReturnsNoCustomerGroupWithoutColon()
    {
        // Arrange
        // Note: this catalog's prefix has NO trailing colon, because there
        // is no payload to delimit. The catalog uses exact equality on the
        // whole prefix string.

        // Act
        var prefix = NoCustomerGroupErrors.Prefix;

        // Assert
        prefix.Should().Be("NoCustomerGroup");
    }

    // ── Format() ────────────────────────────────────────────────────────

    [Fact]
    public void Format_WhenCalled_ReturnsExactlyThePrefix()
    {
        // Arrange
        // No payload — Format() returns the bare prefix.

        // Act
        var result = NoCustomerGroupErrors.Format();

        // Assert
        result.Should().Be(NoCustomerGroupErrors.Prefix);
        result.Should().Be("NoCustomerGroup");
    }

    [Fact]
    public void Format_WhenCalledTwice_ReturnsSameValue()
    {
        // Arrange
        // Format() is pure — two calls must produce the same string.

        // Act
        var first = NoCustomerGroupErrors.Format();
        var second = NoCustomerGroupErrors.Format();

        // Assert
        first.Should().Be(second);
    }

    // ── TryParse() — negative cases ────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenNull_ReturnsFalse()
    {
        // Arrange

        // Act
        var result = NoCustomerGroupErrors.TryParse(null);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenEmptyString_ReturnsFalse()
    {
        // Arrange

        // Act
        var result = NoCustomerGroupErrors.TryParse(string.Empty);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenWhitespaceOnly_ReturnsFalse()
    {
        // Arrange
        // string.IsNullOrEmpty("  ") is false, so the null/empty guard
        // passes; then the equality check ("  " == "NoCustomerGroup") is
        // false → overall false.

        // Act
        var result = NoCustomerGroupErrors.TryParse("  ");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenPrefixWithExtraSuffix_ReturnsFalse()
    {
        // Arrange
        // This catalog uses EXACT equality (==), not StartsWith. So an
        // input with extra characters after "NoCustomerGroup" must NOT
        // match — this is the critical difference from the other catalogs.

        // Act
        var result = NoCustomerGroupErrors.TryParse("NoCustomerGroupExtra");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenPrefixWithTrailingColon_ReturnsFalse()
    {
        // Arrange
        // "NoCustomerGroup:" is NOT equal to "NoCustomerGroup" — the
        // exact-equality check fails. Other catalogs in this namespace
        // (CartConflict, PurchaseLimit, etc.) use StartsWith so the
        // trailing-colon case parses true there; here it must be false.

        // Act
        var result = NoCustomerGroupErrors.TryParse("NoCustomerGroup:");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenUnrelatedError_ReturnsFalse()
    {
        // Arrange

        // Act
        var result = NoCustomerGroupErrors.TryParse("OtherError");

        // Assert
        result.Should().BeFalse();
    }

    // ── TryParse() — positive cases ─────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenExactPrefixString_ReturnsTrue()
    {
        // Arrange
        // The exact prefix string is the canonical Format() output and
        // must parse true.

        // Act
        var result = NoCustomerGroupErrors.TryParse("NoCustomerGroup");

        // Assert
        result.Should().BeTrue();
    }

    // ── Round-trip Format → TryParse ─────────────────────────────────────

    [Fact]
    public void RoundTrip_FormatThenTryParse_ReturnsTrue()
    {
        // Arrange
        var formatted = NoCustomerGroupErrors.Format();

        // Act
        var parsed = NoCustomerGroupErrors.TryParse(formatted);

        // Assert
        parsed.Should().BeTrue();
    }
}

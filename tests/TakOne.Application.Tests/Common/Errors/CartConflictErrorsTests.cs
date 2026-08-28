using FluentAssertions;
using TakOne.Application.Common.Errors;
using Xunit;

namespace TakOne.Application.Tests.Common.Errors;

/// <summary>
/// Unit tests for <see cref="CartConflictErrors"/> — the culture-neutral
/// "cart was modified by another session" stable-code catalog. This catalog
/// has NO payload — <see cref="CartConflictErrors.Format"/> returns just the
/// prefix, and <see cref="CartConflictErrors.TryParse"/> does nothing more
/// than check that the error string starts with the prefix.
/// </summary>
/// <remarks>
/// <b>TEST COUNT REDUCTION (Brutal Code Review v3 finding #29):</b>
/// the previous version of this file had 12 tests covering the literal
/// prefix contract, the Format() happy path, Format() purity (two calls
/// return the same value), TryParse positive/negative cases (null, empty,
/// whitespace, unrelated error, case-sensitivity, missing colon, prefix
/// with extra content), and a Format→TryParse round-trip. The review
/// correctly identified these as "real assertions, but extremely low
/// value — testing that C# string equality works." The catalog is a
/// pair of one-line static helpers; the tests verified C# language
/// semantics, not the catalog's behavior under realistic conditions.
/// Per the review's recommendation, this file now retains only 2
/// tests — the Format() happy-path contract (proves Format returns the
/// literal prefix string) and the Format→TryParse round-trip (proves
/// the catalog's two halves are mutually consistent). Maintenance
/// effort is now focused on the higher-value Sale handler tests, where
/// the real bugs live. The deleted edge-case tests are documented in
/// the Round 18 worklog for traceability.
/// </remarks>
public class CartConflictErrorsTests
{
    // ── Format() happy-path contract ────────────────────────────────────

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

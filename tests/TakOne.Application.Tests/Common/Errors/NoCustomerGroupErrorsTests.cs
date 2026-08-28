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
/// <remarks>
/// <b>TEST COUNT REDUCTION (Brutal Code Review v3 finding #29):</b>
/// the previous version of this file had 11 tests covering the literal
/// prefix contract (noting the no-trailing-colon asymmetry), Format()
/// happy path, Format() purity (two calls return the same value),
/// TryParse positive/negative cases (null, empty, whitespace, prefix
/// with extra suffix, prefix with trailing colon, unrelated error,
/// exact prefix), and a Format→TryParse round-trip. The review
/// correctly identified these as "real assertions, but extremely low
/// value — testing that C# string equality works." The catalog is a
/// pair of one-line static helpers; the tests verified C# language
/// semantics, not the catalog's behavior under realistic conditions.
/// Per the review's recommendation, this file now retains only 2 tests
/// — the Format() happy-path contract (proves Format returns the
/// literal "NoCustomerGroup" string) and the Format→TryParse round-trip
/// (proves the catalog's two halves are mutually consistent, including
/// the exact-equality asymmetry). Maintenance effort is now focused on
/// the higher-value Sale handler tests, where the real bugs live. The
/// deleted edge-case tests are documented in the Round 18 worklog for
/// traceability.
/// </remarks>
public class NoCustomerGroupErrorsTests
{
    // ── Format() happy-path contract ────────────────────────────────────

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

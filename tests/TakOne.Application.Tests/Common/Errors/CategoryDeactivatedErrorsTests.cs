using FluentAssertions;
using TakOne.Application.Common.Errors;
using Xunit;

namespace TakOne.Application.Tests.Common.Errors;

/// <summary>
/// Unit tests for <see cref="CategoryDeactivatedErrors"/> — the
/// culture-neutral "product's category (or sub/sub-sub) is deactivated"
/// stable-code catalog. Format produces "CategoryDeactivated:{productName}"
/// and TryParse extracts everything after the prefix (no further splitting).
/// </summary>
/// <remarks>
/// <b>TEST COUNT REDUCTION (Brutal Code Review v3 finding #29):</b>
/// the previous version of this file had 12 tests covering the literal
/// prefix contract, Format() with ASCII/Persian/colon-containing product
/// names, TryParse positive/negative cases (null, empty, unrelated,
/// empty payload after prefix, ASCII, Persian, colon-containing), and
/// a Format→TryParse round-trip. The review correctly identified these
/// as "real assertions, but extremely low value — testing that C#
/// string equality works." The catalog is a pair of one-line static
/// helpers; the tests verified C# language semantics, not the catalog's
/// behavior under realistic conditions. Per the review's recommendation,
/// this file now retains only 2 tests — the Format() happy-path contract
/// (proves Format returns the literal "CategoryDeactivated:Apple" string
/// for an ASCII product name) and the Format→TryParse round-trip (proves
/// the catalog's two halves are mutually consistent). Maintenance effort
/// is now focused on the higher-value Sale handler tests, where the real
/// bugs live. The deleted edge-case tests are documented in the Round 18
/// worklog for traceability.
/// </remarks>
public class CategoryDeactivatedErrorsTests
{
    // ── Format() happy-path contract ────────────────────────────────────

    [Fact]
    public void Format_WhenGivenAsciiProductName_ReturnsExpectedString()
    {
        // Arrange
        const string productName = "Apple";

        // Act
        var result = CategoryDeactivatedErrors.Format(productName);

        // Assert
        result.Should().Be("CategoryDeactivated:Apple");
    }

    // ── Round-trip Format → TryParse ─────────────────────────────────────

    [Fact]
    public void RoundTrip_FormatThenTryParse_ReturnsOriginalName()
    {
        // Arrange
        const string productName = "Apple";
        var formatted = CategoryDeactivatedErrors.Format(productName);

        // Act
        var ok = CategoryDeactivatedErrors.TryParse(formatted, out var parsedName);

        // Assert
        ok.Should().BeTrue();
        parsedName.Should().Be(productName);
    }
}

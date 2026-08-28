using FluentAssertions;
using TakOne.Application.Common.Errors;
using Xunit;

namespace TakOne.Application.Tests.Common.Errors;

/// <summary>
/// Unit tests for <see cref="PurchaseLimitErrors"/> — the culture-neutral
/// "purchase limit exceeded" stable-code catalog. Format produces
/// "PurchaseLimitExceeded:{productName}|{limit}" and TryParse splits on
/// the LAST '|' so a product name containing a pipe still parses.
/// </summary>
/// <remarks>
/// <b>TEST COUNT REDUCTION (Brutal Code Review v3 finding #29):</b>
/// the previous version of this file had 15 tests covering the literal
/// prefix contract, Format() with ASCII/Persian/pipe-containing product
/// names, TryParse positive/negative cases (null, empty, unrelated,
/// non-integer limit, no-pipe payload, empty limit part, ASCII,
/// pipe-containing name, Persian name, zero-limit), and a
/// Format→TryParse round-trip. The review correctly identified these
/// as "real assertions, but extremely low value — testing that C#
/// string equality works." The catalog is a pair of one-line static
/// helpers; the tests verified C# language semantics, not the catalog's
/// behavior under realistic conditions. Per the review's recommendation,
/// this file now retains only 2 tests — the Format() happy-path contract
/// (proves Format returns the literal "PurchaseLimitExceeded:Apple|5"
/// string for ASCII inputs) and the Format→TryParse round-trip (proves
/// the catalog's two halves are mutually consistent). Maintenance effort
/// is now focused on the higher-value Sale handler tests, where the real
/// bugs live. The deleted edge-case tests are documented in the Round 18
/// worklog for traceability.
/// </remarks>
public class PurchaseLimitErrorsTests
{
    // ── Format() happy-path contract ────────────────────────────────────

    [Fact]
    public void Format_WhenGivenAsciiProductName_ReturnsExpectedString()
    {
        // Arrange
        const string productName = "Apple";
        const int limit = 5;

        // Act
        var result = PurchaseLimitErrors.Format(productName, limit);

        // Assert
        result.Should().Be("PurchaseLimitExceeded:Apple|5");
    }

    // ── Round-trip Format → TryParse ─────────────────────────────────────

    [Fact]
    public void RoundTrip_FormatThenTryParse_ReturnsOriginalNameAndLimit()
    {
        // Arrange
        const string productName = "Apple";
        const int limit = 5;
        var formatted = PurchaseLimitErrors.Format(productName, limit);

        // Act
        var ok = PurchaseLimitErrors.TryParse(formatted, out var parsedName, out var parsedLimit);

        // Assert
        ok.Should().BeTrue();
        parsedName.Should().Be(productName);
        parsedLimit.Should().Be(limit);
    }
}

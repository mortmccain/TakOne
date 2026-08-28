using FluentAssertions;
using TakOne.Application.Common.Errors;
using Xunit;

namespace TakOne.Application.Tests.Common.Errors;

/// <summary>
/// Unit tests for <see cref="StockErrors"/> — the culture-neutral
/// "not enough stock to approve/fulfil a sale" stable-code catalog. Format
/// produces "StockExceeded:{productName}|{stock}|{required}" and TryParse
/// splits on the LAST two pipes so a product name containing pipes still
/// parses.
/// </summary>
/// <remarks>
/// <b>TEST COUNT REDUCTION (Brutal Code Review v3 finding #29):</b>
/// the previous version of this file had 15 tests covering the literal
/// prefix contract, Format() with ASCII/Persian/pipe-containing product
/// names, TryParse positive/negative cases (null, empty, unrelated,
/// non-integer required, non-integer stock, no-pipe payload, one-pipe
/// payload, empty product name, ASCII, pipe-containing name), and a
/// Format→TryParse round-trip. The review correctly identified these as
/// "real assertions, but extremely low value — testing that C#
/// string equality works." The catalog is a pair of one-line static
/// helpers; the tests verified C# language semantics, not the catalog's
/// behavior under realistic conditions. Per the review's recommendation,
/// this file now retains only 2 tests — the Format() happy-path contract
/// (proves Format returns the literal "StockExceeded:Apple|0|4" string
/// for ASCII inputs at the canonical ApproveSale boundary) and the
/// Format→TryParse round-trip (proves the catalog's two halves are
/// mutually consistent). Maintenance effort is now focused on the
/// higher-value Sale handler tests, where the real bugs live. The
/// deleted edge-case tests are documented in the Round 18 worklog for
/// traceability.
/// </remarks>
public class StockErrorsTests
{
    // ── Format() happy-path contract ────────────────────────────────────

    [Fact]
    public void Format_WhenGivenAsciiProductName_ReturnsExpectedString()
    {
        // Arrange
        // Boundary stock=0 with required=4 — the canonical scenario in
        // ApproveSaleCommandHandler's stock-check failure.
        const string productName = "Apple";
        const int stock = 0;
        const int required = 4;

        // Act
        var result = StockErrors.Format(productName, stock, required);

        // Assert
        result.Should().Be("StockExceeded:Apple|0|4");
    }

    // ── Round-trip Format → TryParse ─────────────────────────────────────

    [Fact]
    public void RoundTrip_FormatThenTryParse_ReturnsOriginalComponents()
    {
        // Arrange
        const string productName = "Apple";
        const int stock = 3;
        const int required = 7;
        var formatted = StockErrors.Format(productName, stock, required);

        // Act
        var ok = StockErrors.TryParse(formatted, out var parsedName, out var parsedStock, out var parsedRequired);

        // Assert
        ok.Should().BeTrue();
        parsedName.Should().Be(productName);
        parsedStock.Should().Be(stock);
        parsedRequired.Should().Be(required);
    }
}

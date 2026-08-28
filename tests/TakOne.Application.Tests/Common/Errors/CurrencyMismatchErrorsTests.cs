using FluentAssertions;
using TakOne.Application.Common.Errors;
using Xunit;

namespace TakOne.Application.Tests.Common.Errors;

/// <summary>
/// Unit tests for <see cref="CurrencyMismatchErrors"/> — the culture-neutral
/// "currency mismatch" stable-code catalog. Format produces
/// "CurrencyMismatch:{productName}|{productCurrency}|{salaryCurrency}" and
/// TryParse splits on the LAST 2 pipes so a product name containing pipes
/// still parses.
/// </summary>
/// <remarks>
/// <b>TEST COUNT REDUCTION (Brutal Code Review v3 finding #29):</b>
/// the previous version of this file had 12 tests covering the literal
/// prefix contract, Format() with ASCII/Persian/pipe-containing product
/// names, TryParse positive/negative cases (null, empty, unrelated,
/// one-pipe payload, ASCII, pipe-containing name, more-than-three-pipe
/// payload), and a Format→TryParse round-trip. The review correctly
/// identified these as "real assertions, but extremely low value —
/// testing that C# string equality works." The catalog is a pair of
/// one-line static helpers; the tests verified C# language semantics,
/// not the catalog's behavior under realistic conditions. Per the
/// review's recommendation, this file now retains only 2 tests — the
/// Format() happy-path contract (proves Format returns the literal
/// "CurrencyMismatch:Apple|USD|IRR" string for ASCII inputs) and the
/// Format→TryParse round-trip (proves the catalog's two halves are
/// mutually consistent). Maintenance effort is now focused on the
/// higher-value Sale handler tests, where the real bugs live. The
/// deleted edge-case tests are documented in the Round 18 worklog for
/// traceability.
/// </remarks>
public class CurrencyMismatchErrorsTests
{
    // ── Format() happy-path contract ────────────────────────────────────

    [Fact]
    public void Format_WhenGivenAsciiName_ReturnsExpectedString()
    {
        // Arrange
        const string productName = "Apple";
        const string productCurrency = "USD";
        const string salaryCurrency = "IRR";

        // Act
        var result = CurrencyMismatchErrors.Format(productName, productCurrency, salaryCurrency);

        // Assert
        result.Should().Be("CurrencyMismatch:Apple|USD|IRR");
    }

    // ── Round-trip Format → TryParse ─────────────────────────────────────

    [Fact]
    public void RoundTrip_FormatThenTryParse_ReturnsOriginalComponents()
    {
        // Arrange
        const string productName = "Apple";
        const string productCurrency = "USD";
        const string salaryCurrency = "IRR";
        var formatted = CurrencyMismatchErrors.Format(productName, productCurrency, salaryCurrency);

        // Act
        var ok = CurrencyMismatchErrors.TryParse(formatted, out var parsedName, out var parsedPc, out var parsedSc);

        // Assert
        ok.Should().BeTrue();
        parsedName.Should().Be(productName);
        parsedPc.Should().Be(productCurrency);
        parsedSc.Should().Be(salaryCurrency);
    }
}

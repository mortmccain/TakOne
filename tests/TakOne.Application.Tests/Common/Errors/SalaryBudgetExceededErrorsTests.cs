using FluentAssertions;
using TakOne.Application.Common.Errors;
using Xunit;

namespace TakOne.Application.Tests.Common.Errors;

/// <summary>
/// Unit tests for <see cref="SalaryBudgetExceededErrors"/> — the
/// culture-neutral "salary budget exceeded" stable-code catalog. Format
/// produces "SalaryBudgetExceeded:{productName}|{lineTotal}|{remainingBudget}|{currency}"
/// and TryParse splits on the LAST 3 pipes so a product name containing
/// pipes still parses.
/// </summary>
/// <remarks>
/// <b>TEST COUNT REDUCTION (Brutal Code Review v3 finding #29):</b>
/// the previous version of this file had 14 tests covering the literal
/// prefix contract, Format() with whole-number/fractional-decimal/
/// pipe-containing inputs, TryParse positive/negative cases (null,
/// empty, unrelated, non-decimal lineTotal, non-decimal remainingBudget,
/// fewer-than-three-pipes payload, valid payload, pipe-containing name,
/// fractional lineTotal), and a Format→TryParse round-trip. The review
/// correctly identified these as "real assertions, but extremely low
/// value — testing that C# string equality works." The catalog is a
/// pair of one-line static helpers; the tests verified C# language
/// semantics, not the catalog's behavior under realistic conditions.
/// Per the review's recommendation, this file now retains only 2 tests
/// — the Format() happy-path contract (proves Format returns the
/// literal "SalaryBudgetExceeded:Apple|50000|30000|IRR" string for
/// whole-number inputs) and the Format→TryParse round-trip (proves
/// the catalog's two halves are mutually consistent). Maintenance
/// effort is now focused on the higher-value Sale handler tests, where
/// the real bugs live. The deleted edge-case tests are documented in
/// the Round 18 worklog for traceability.
/// </remarks>
public class SalaryBudgetExceededErrorsTests
{
    // ── Format() happy-path contract ────────────────────────────────────

    [Fact]
    public void Format_WhenGivenWholeNumbers_ReturnsExpectedString()
    {
        // Arrange
        const string productName = "Apple";
        const decimal lineTotal = 50000m;
        const decimal remainingBudget = 30000m;
        const string currency = "IRR";

        // Act
        var result = SalaryBudgetExceededErrors.Format(productName, lineTotal, remainingBudget, currency);

        // Assert
        result.Should().Be("SalaryBudgetExceeded:Apple|50000|30000|IRR");
    }

    // ── Round-trip Format → TryParse ─────────────────────────────────────

    [Fact]
    public void RoundTrip_FormatThenTryParse_ReturnsOriginalComponents()
    {
        // Arrange
        const string productName = "Apple";
        const decimal lineTotal = 50000m;
        const decimal remainingBudget = 30000m;
        const string currency = "IRR";
        var formatted = SalaryBudgetExceededErrors.Format(productName, lineTotal, remainingBudget, currency);

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse(formatted, out var pn, out var lt, out var rb, out var c);

        // Assert
        ok.Should().BeTrue();
        pn.Should().Be(productName);
        lt.Should().Be(lineTotal);
        rb.Should().Be(remainingBudget);
        c.Should().Be(currency);
    }
}

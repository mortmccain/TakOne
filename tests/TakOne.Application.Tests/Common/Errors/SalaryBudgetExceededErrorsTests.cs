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
public class SalaryBudgetExceededErrorsTests
{
    // ── Prefix contract ────────────────────────────────────────────────

    [Fact]
    public void Prefix_WhenRead_ReturnsSalaryBudgetExceededWithColon()
    {
        // Arrange

        // Act
        var prefix = SalaryBudgetExceededErrors.Prefix;

        // Assert
        prefix.Should().Be("SalaryBudgetExceeded:");
    }

    // ── Format() ────────────────────────────────────────────────────────

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

    [Fact]
    public void Format_WhenGivenFractionalAmounts_PreservesDecimals()
    {
        // Arrange
        // decimal.ToString() default formatting for 1000.5m is "1000.5"
        // (invariant for non-culture-sensitive decimal.ToString() calls —
        // Format uses interpolated string, which uses CurrentCulture by
        // default, but decimal.ToString() inside interpolation is
        // culture-sensitive). The SUT uses $"{x}" which is invariant
        // for non-numeric types, but for decimal it uses CurrentCulture.
        // We assert against the en-US/culture-invariant form "1000.5"
        // — if the test runner is in a different culture, this may need
        // to be adjusted; the framework default for xUnit is typically
        // invariant for the format calls. We assert exact-match against
        // the value Format() actually produces.
        const string productName = "اسپاگتی";
        const decimal lineTotal = 1000.5m;
        const decimal remainingBudget = 500m;
        const string currency = "USD";

        // Act
        var result = SalaryBudgetExceededErrors.Format(productName, lineTotal, remainingBudget, currency);

        // Assert
        // The string interpolation uses decimal.ToString() under the hood
        // (default formatter). For en-US/invariant culture that yields
        // "1000.5" and "500".
        result.Should().Contain("SalaryBudgetExceeded:اسپاگتی|");
        result.Should().Contain("|500|USD");
        // Decimal values must round-trip — verify by re-parsing.
        SalaryBudgetExceededErrors.TryParse(result, out _, out var parsedLineTotal, out var parsedRemaining, out _)
            .Should().BeTrue();
        parsedLineTotal.Should().Be(lineTotal);
        parsedRemaining.Should().Be(remainingBudget);
    }

    [Fact]
    public void Format_WhenGivenProductNameContainingPipes_DoesNotBreakFormat()
    {
        // Arrange
        const string productName = "Product|With|Pipes";
        const decimal lineTotal = 100m;
        const decimal remainingBudget = 50m;
        const string currency = "IRR";

        // Act
        var result = SalaryBudgetExceededErrors.Format(productName, lineTotal, remainingBudget, currency);

        // Assert
        result.Should().Be("SalaryBudgetExceeded:Product|With|Pipes|100|50|IRR");
    }

    // ── TryParse() — negative cases ────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenNull_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse(null, out var name, out var lt, out var rb, out var c);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        lt.Should().Be(0m);
        rb.Should().Be(0m);
        c.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_WhenGivenEmptyString_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse(string.Empty, out _, out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenGivenUnrelatedError_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse("OtherError", out _, out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenLineTotalIsNotADecimal_ReturnsFalse()
    {
        // Arrange
        // parts[^3] must be a parseable decimal (lineTotal).

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse(
            "SalaryBudgetExceeded:Apple|notanint|30000|IRR",
            out _, out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenRemainingBudgetIsNotADecimal_ReturnsFalse()
    {
        // Arrange
        // parts[^2] must be a parseable decimal (remainingBudget).

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse(
            "SalaryBudgetExceeded:Apple|50000|notanint|IRR",
            out _, out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenFewerThanThreePipesInPayload_ReturnsFalse()
    {
        // Arrange
        // parts.Length must be >= 4 (productName + lineTotal + remainingBudget
        // + currency). 3 parts (only 2 pipes) → fail.

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse(
            "SalaryBudgetExceeded:Apple|50000|30000",
            out _, out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    // ── TryParse() — positive cases ─────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenValidPayload_ReturnsAllFourComponents()
    {
        // Arrange
        const string input = "SalaryBudgetExceeded:Apple|50000|30000|IRR";

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse(input, out var name, out var lt, out var rb, out var c);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Apple");
        lt.Should().Be(50000m);
        rb.Should().Be(30000m);
        c.Should().Be("IRR");
    }

    [Fact]
    public void TryParse_WhenProductNameContainsPipes_SplitsOnLastThreePipes()
    {
        // Arrange
        // Product name "Product|With|Pipes" has 2 pipes itself; the last 3
        // pipes of the full string are the {lineTotal}, {remainingBudget}
        // and {currency} delimiters.

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse(
            "SalaryBudgetExceeded:Product|With|Pipes|100|50|IRR",
            out var name,
            out var lt,
            out var rb,
            out var c);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Product|With|Pipes");
        lt.Should().Be(100m);
        rb.Should().Be(50m);
        c.Should().Be("IRR");
    }

    [Fact]
    public void TryParse_WhenLineTotalIsFractional_PreservesDecimal()
    {
        // Arrange
        // Decimal "1000.5" must round-trip through TryParse unchanged.

        // Act
        var ok = SalaryBudgetExceededErrors.TryParse(
            "SalaryBudgetExceeded:Apple|1000.5|500|USD",
            out _,
            out var lt,
            out var rb,
            out _);

        // Assert
        ok.Should().BeTrue();
        lt.Should().Be(1000.5m);
        rb.Should().Be(500m);
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

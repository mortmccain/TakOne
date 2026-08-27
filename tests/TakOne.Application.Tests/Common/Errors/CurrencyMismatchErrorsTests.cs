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
public class CurrencyMismatchErrorsTests
{
    // ── Prefix contract ────────────────────────────────────────────────

    [Fact]
    public void Prefix_WhenRead_ReturnsCurrencyMismatchWithColon()
    {
        // Arrange

        // Act
        var prefix = CurrencyMismatchErrors.Prefix;

        // Assert
        prefix.Should().Be("CurrencyMismatch:");
    }

    // ── Format() ────────────────────────────────────────────────────────

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

    [Fact]
    public void Format_WhenGivenPersianName_PreservesUnicode()
    {
        // Arrange
        const string productName = "اسپاگتی";
        const string productCurrency = "IRR";
        const string salaryCurrency = "USD";

        // Act
        var result = CurrencyMismatchErrors.Format(productName, productCurrency, salaryCurrency);

        // Assert
        result.Should().Be("CurrencyMismatch:اسپاگتی|IRR|USD");
    }

    [Fact]
    public void Format_WhenGivenProductNameContainingPipes_DoesNotBreakFormat()
    {
        // Arrange
        // The product name can contain pipes; Format() embeds it verbatim
        // and TryParse splits on the LAST 2 pipes.

        // Act
        var result = CurrencyMismatchErrors.Format("Product|With|Pipes", "USD", "IRR");

        // Assert
        result.Should().Be("CurrencyMismatch:Product|With|Pipes|USD|IRR");
    }

    // ── TryParse() — negative cases ────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenNull_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = CurrencyMismatchErrors.TryParse(null, out var name, out var pc, out var sc);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        pc.Should().BeEmpty();
        sc.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_WhenGivenEmptyString_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = CurrencyMismatchErrors.TryParse(string.Empty, out var name, out var pc, out var sc);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        pc.Should().BeEmpty();
        sc.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_WhenGivenUnrelatedError_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = CurrencyMismatchErrors.TryParse("OtherError", out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenOnlyOnePipeInPayload_ReturnsFalse()
    {
        // Arrange
        // parts.Length must be >= 3 (one productCurrency + one
        // salaryCurrency + at least one name part). One pipe → 2 parts →
        // fail.

        // Act
        var ok = CurrencyMismatchErrors.TryParse("CurrencyMismatch:Apple|USD", out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    // ── TryParse() — positive cases ─────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenValidAsciiName_ReturnsAllThreeComponents()
    {
        // Arrange
        const string input = "CurrencyMismatch:Apple|USD|IRR";

        // Act
        var ok = CurrencyMismatchErrors.TryParse(input, out var name, out var pc, out var sc);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Apple");
        pc.Should().Be("USD");
        sc.Should().Be("IRR");
    }

    [Fact]
    public void TryParse_WhenProductNameContainsPipes_SplitsOnLastTwoPipes()
    {
        // Arrange
        // The product name "Product|With|Pipes" has 2 pipes itself; the
        // last 2 pipes of the full string are the {productCurrency} and
        // {salaryCurrency} delimiters.

        // Act
        var ok = CurrencyMismatchErrors.TryParse(
            "CurrencyMismatch:Product|With|Pipes|USD|IRR",
            out var name,
            out var pc,
            out var sc);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Product|With|Pipes");
        pc.Should().Be("USD");
        sc.Should().Be("IRR");
    }

    [Fact]
    public void TryParse_WhenPayloadHasMoreThanThreePipes_JoinsExtrasBackIntoName()
    {
        // Arrange
        // 5 parts total → parts.Length (5) >= 3 → returns true. The last
        // 2 parts are the currencies; everything before is joined back
        // into productName with '|'.

        // Act
        var ok = CurrencyMismatchErrors.TryParse(
            "CurrencyMismatch:Apple|USD|IRR|Extra|Parts",
            out var name,
            out var pc,
            out var sc);

        // Assert
        // salaryCurrency = parts[^1] = "Parts"
        // productCurrency = parts[^2] = "Extra"
        // productName = Join(parts[0..2]) = "Apple|USD|IRR"
        ok.Should().BeTrue();
        name.Should().Be("Apple|USD|IRR");
        pc.Should().Be("Extra");
        sc.Should().Be("Parts");
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

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
public class StockErrorsTests
{
    // ── Prefix contract ────────────────────────────────────────────────

    [Fact]
    public void Prefix_WhenRead_ReturnsStockExceededWithColon()
    {
        // Arrange

        // Act
        var prefix = StockErrors.Prefix;

        // Assert
        prefix.Should().Be("StockExceeded:");
    }

    // ── Format() ────────────────────────────────────────────────────────

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

    [Fact]
    public void Format_WhenGivenPersianProductName_PreservesUnicode()
    {
        // Arrange
        const string productName = "اسپاگتی";
        const int stock = 5;
        const int required = 10;

        // Act
        var result = StockErrors.Format(productName, stock, required);

        // Assert
        result.Should().Be("StockExceeded:اسپاگتی|5|10");
    }

    [Fact]
    public void Format_WhenGivenProductNameContainingPipes_DoesNotBreakFormat()
    {
        // Arrange
        // The product name "Product|With|Pipes" contains pipes; Format()
        // must embed it verbatim — TryParse splits on the LAST 2 pipes.

        // Act
        var result = StockErrors.Format("Product|With|Pipes", 0, 4);

        // Assert
        result.Should().Be("StockExceeded:Product|With|Pipes|0|4");
    }

    // ── TryParse() — negative cases ────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenNull_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = StockErrors.TryParse(null, out var name, out var stock, out var required);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        stock.Should().Be(0);
        required.Should().Be(0);
    }

    [Fact]
    public void TryParse_WhenGivenEmptyString_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = StockErrors.TryParse(string.Empty, out var name, out var stock, out var required);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        stock.Should().Be(0);
        required.Should().Be(0);
    }

    [Fact]
    public void TryParse_WhenGivenUnrelatedError_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = StockErrors.TryParse("OtherError", out var name, out var stock, out var required);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        stock.Should().Be(0);
        required.Should().Be(0);
    }

    [Fact]
    public void TryParse_WhenRequiredPartIsNotAnInteger_ReturnsFalse()
    {
        // Arrange
        // The part after the LAST pipe must be the {required} integer.

        // Act
        var ok = StockErrors.TryParse("StockExceeded:Apple|0|notanint", out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenStockPartIsNotAnInteger_ReturnsFalse()
    {
        // Arrange
        // After splitting off {required}, the part after the second-to-last
        // pipe must be the {stock} integer.

        // Act
        var ok = StockErrors.TryParse("StockExceeded:Apple|notanint|4", out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenNoPipeInPayload_ReturnsFalse()
    {
        // Arrange
        // No pipes → cannot split off {required}.

        // Act
        var ok = StockErrors.TryParse("StockExceeded:Apple", out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenOnlyOnePipeInPayload_ReturnsFalse()
    {
        // Arrange
        // One pipe is enough to split off {required}, but then the remainder
        // has no pipe to split off {stock} → secondPipe < 0 → false.

        // Act
        var ok = StockErrors.TryParse("StockExceeded:Apple|0", out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenProductNameIsEmpty_ReturnsFalse()
    {
        // Arrange
        // After splitting off {required} and {stock}, the remaining
        // productName must be non-empty (length > 0).

        // Act
        var ok = StockErrors.TryParse("StockExceeded:|0|4", out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    // ── TryParse() — positive cases ─────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenValidAsciiName_ReturnsAllThreeComponents()
    {
        // Arrange
        const string input = "StockExceeded:Apple|0|4";

        // Act
        var ok = StockErrors.TryParse(input, out var name, out var stock, out var required);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Apple");
        stock.Should().Be(0);
        required.Should().Be(4);
    }

    [Fact]
    public void TryParse_WhenProductNameContainsPipes_SplitsOnLastTwoPipes()
    {
        // Arrange
        // "StockExceeded:Product|With|Pipes|0|4" — the product name is
        // "Product|With|Pipes" (containing 2 pipes), and the last 2 pipes
        // are the {stock}|{required} delimiters.

        // Act
        var ok = StockErrors.TryParse(
            "StockExceeded:Product|With|Pipes|0|4",
            out var name,
            out var stock,
            out var required);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Product|With|Pipes");
        stock.Should().Be(0);
        required.Should().Be(4);
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

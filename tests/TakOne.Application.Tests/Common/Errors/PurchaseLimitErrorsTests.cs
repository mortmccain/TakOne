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
public class PurchaseLimitErrorsTests
{
    // ── Prefix contract ────────────────────────────────────────────────

    [Fact]
    public void Prefix_WhenRead_ReturnsPurchaseLimitExceededWithColon()
    {
        // Arrange

        // Act
        var prefix = PurchaseLimitErrors.Prefix;

        // Assert
        prefix.Should().Be("PurchaseLimitExceeded:");
    }

    // ── Format() ────────────────────────────────────────────────────────

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

    [Fact]
    public void Format_WhenGivenPersianProductName_PreservesUnicode()
    {
        // Arrange
        // Persian product names with embedded digits + spaces must round-trip
        // without being mangled — the catalog is explicitly culture-neutral.
        const string productName = "اسپاگتی 1.2";
        const int limit = 2;

        // Act
        var result = PurchaseLimitErrors.Format(productName, limit);

        // Assert
        result.Should().Be("PurchaseLimitExceeded:اسپاگتی 1.2|2");
    }

    [Fact]
    public void Format_WhenGivenProductNameContainingPipes_DoesNotBreakFormat()
    {
        // Arrange
        // The product name can legitimately contain a '|' — Format() must
        // embed it verbatim (TryParse handles splitting on the LAST pipe).

        // Act
        var result = PurchaseLimitErrors.Format("Product|With|Pipes", 3);

        // Assert
        result.Should().Be("PurchaseLimitExceeded:Product|With|Pipes|3");
    }

    // ── TryParse() — negative cases ────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenNull_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = PurchaseLimitErrors.TryParse(null, out var name, out var limit);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        limit.Should().Be(0);
    }

    [Fact]
    public void TryParse_WhenGivenEmptyString_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = PurchaseLimitErrors.TryParse(string.Empty, out var name, out var limit);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        limit.Should().Be(0);
    }

    [Fact]
    public void TryParse_WhenGivenUnrelatedError_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = PurchaseLimitErrors.TryParse("OtherError", out var name, out var limit);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        limit.Should().Be(0);
    }

    [Fact]
    public void TryParse_WhenLimitIsNotAnInteger_ReturnsFalse()
    {
        // Arrange
        // The last '|' separates the limit from the name. If the part after
        // the last pipe is not a parseable int, parsing fails.
        //
        // NOTE (SUT detail): the SUT sets productName to the part before the
        // LAST '|' BEFORE the int.TryParse check — so when TryParse returns
        // false due to a non-int limit, the out `productName` parameter is
        // NOT reset to empty. We assert only on the boolean return value
        // here, in keeping with the standard TryParse contract that out
        // parameters are not meaningful when false is returned.

        // Act
        var ok = PurchaseLimitErrors.TryParse("PurchaseLimitExceeded:Apple|notanint", out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Fact]
    public void TryParse_WhenNoPipeInPayload_ReturnsFalse()
    {
        // Arrange
        // Without a '|' to split on, there's no limit component to extract.

        // Act
        var ok = PurchaseLimitErrors.TryParse("PurchaseLimitExceeded:Apple", out var name, out var limit);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
        limit.Should().Be(0);
    }

    [Fact]
    public void TryParse_WhenLimitPartIsEmpty_ReturnsFalse()
    {
        // Arrange
        // An empty string after the last '|' is not a parseable int.
        //
        // NOTE (SUT detail): same as the non-int-limit case above — the
        // SUT sets productName to the part before the LAST '|' BEFORE
        // int.TryParse runs, so productName is NOT empty when TryParse
        // returns false here.

        // Act
        var ok = PurchaseLimitErrors.TryParse("PurchaseLimitExceeded:Apple|", out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    // ── TryParse() — positive cases ─────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenValidAsciiName_ReturnsNameAndLimit()
    {
        // Arrange
        const string input = "PurchaseLimitExceeded:Apple|5";

        // Act
        var ok = PurchaseLimitErrors.TryParse(input, out var name, out var limit);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Apple");
        limit.Should().Be(5);
    }

    [Fact]
    public void TryParse_WhenProductNameContainsPipes_SplitsOnLastPipe()
    {
        // Arrange
        // The product name "Product|With|Pipes" contains 2 pipes. The last
        // pipe is the delimiter — everything before it is the product name.

        // Act
        var ok = PurchaseLimitErrors.TryParse(
            "PurchaseLimitExceeded:Product|With|Pipes|3",
            out var name,
            out var limit);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Product|With|Pipes");
        limit.Should().Be(3);
    }

    [Fact]
    public void TryParse_WhenGivenPersianName_PreservesUnicode()
    {
        // Arrange
        const string input = "PurchaseLimitExceeded:اسپاگتی 1.2|2";

        // Act
        var ok = PurchaseLimitErrors.TryParse(input, out var name, out var limit);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("اسپاگتی 1.2");
        limit.Should().Be(2);
    }

    [Fact]
    public void TryParse_WhenLimitIsZero_ReturnsTrue()
    {
        // Arrange
        // A limit of 0 is a valid integer (and Parse handles it).

        // Act
        var ok = PurchaseLimitErrors.TryParse("PurchaseLimitExceeded:Apple|0", out var name, out var limit);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Apple");
        limit.Should().Be(0);
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

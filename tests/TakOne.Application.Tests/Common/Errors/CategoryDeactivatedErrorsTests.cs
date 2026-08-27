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
public class CategoryDeactivatedErrorsTests
{
    // ── Prefix contract ────────────────────────────────────────────────

    [Fact]
    public void Prefix_WhenRead_ReturnsCategoryDeactivatedWithColon()
    {
        // Arrange

        // Act
        var prefix = CategoryDeactivatedErrors.Prefix;

        // Assert
        prefix.Should().Be("CategoryDeactivated:");
    }

    // ── Format() ────────────────────────────────────────────────────────

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

    [Fact]
    public void Format_WhenGivenPersianProductName_PreservesUnicode()
    {
        // Arrange
        const string productName = "اسپاگتی";

        // Act
        var result = CategoryDeactivatedErrors.Format(productName);

        // Assert
        result.Should().Be("CategoryDeactivated:اسپاگتی");
    }

    [Fact]
    public void Format_WhenGivenProductNameContainingColons_DoesNotBreakFormat()
    {
        // Arrange
        // Product names containing ':' are not split by Format — Format
        // just concatenates. TryParse splits on the FIRST ':' (prefix
        // boundary) and takes everything after as productName.

        // Act
        var result = CategoryDeactivatedErrors.Format("Product:With:Colon");

        // Assert
        result.Should().Be("CategoryDeactivated:Product:With:Colon");
    }

    // ── TryParse() — negative cases ────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenNull_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = CategoryDeactivatedErrors.TryParse(null, out var name);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_WhenGivenEmptyString_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = CategoryDeactivatedErrors.TryParse(string.Empty, out var name);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_WhenGivenUnrelatedError_ReturnsFalse()
    {
        // Arrange

        // Act
        var ok = CategoryDeactivatedErrors.TryParse("OtherError", out var name);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_WhenPayloadAfterPrefixIsEmpty_ReturnsFalse()
    {
        // Arrange
        // The productName.Length > 0 check rejects an empty payload.

        // Act
        var ok = CategoryDeactivatedErrors.TryParse("CategoryDeactivated:", out var name);

        // Assert
        ok.Should().BeFalse();
        name.Should().BeEmpty();
    }

    // ── TryParse() — positive cases ─────────────────────────────────────

    [Fact]
    public void TryParse_WhenGivenValidAsciiName_ReturnsName()
    {
        // Arrange
        const string input = "CategoryDeactivated:Apple";

        // Act
        var ok = CategoryDeactivatedErrors.TryParse(input, out var name);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Apple");
    }

    [Fact]
    public void TryParse_WhenProductNameContainsColons_TakesEverythingAfterPrefix()
    {
        // Arrange
        // The implementation does NOT split on ':' after the prefix — it
        // takes the entire substring after the prefix as the productName.

        // Act
        var ok = CategoryDeactivatedErrors.TryParse(
            "CategoryDeactivated:Product:With:Colon",
            out var name);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("Product:With:Colon");
    }

    [Fact]
    public void TryParse_WhenGivenPersianName_PreservesUnicode()
    {
        // Arrange
        const string input = "CategoryDeactivated:اسپاگتی";

        // Act
        var ok = CategoryDeactivatedErrors.TryParse(input, out var name);

        // Assert
        ok.Should().BeTrue();
        name.Should().Be("اسپاگتی");
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

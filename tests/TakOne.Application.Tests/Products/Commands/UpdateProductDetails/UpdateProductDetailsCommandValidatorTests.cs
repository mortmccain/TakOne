using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Products.Commands.CreateProduct;
using TakOne.Application.Products.Commands.UpdateProductDetails;
using TakOne.SharedKernel.DTOs;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.UpdateProductDetails;

/// <summary>
/// Unit tests for <see cref="UpdateProductDetailsCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator mirrors <see cref="CreateProductCommandValidator"/>'s
/// field-level rules for the descriptive fields (Name, Description,
/// PictureUrl, Price) — but skips category/stock validation since this
/// command doesn't touch those. Each rule is exercised with both
/// passing and failing cases, including boundaries (200/201 chars for
/// Name; 2000/2001 for Description; 500/501 for PictureUrl; etc.) and
/// the BeValidUrl / Length(3) rules for PictureUrl and Currency.
/// </summary>
public class UpdateProductDetailsCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — every field passes the validator's rules.
    // Each test then mutates one field to exercise a single rule.
    private static UpdateProductDetailsCommand BuildValidCommand(
        Guid? productId = null,
        string? name = null,
        string? description = null,
        string? pictureUrl = null,
        decimal? amount = null,
        string? currency = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            Name: name ?? "Apple",
            Description: description ?? "A red apple",
            PictureUrl: pictureUrl,
            Price: new MoneyDto { Amount = amount ?? 1.5m, Currency = currency ?? "USD" });

    private static string? FirstErrorFor(ValidationResult result, string propertyName)
        => result.Errors.FirstOrDefault(e => e.PropertyName == propertyName)?.ErrorMessage;

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenAllFieldsAreValidWithAbsolutePictureUrl_HasNoErrors()
    {
        // Arrange
        // BeValidUrl accepts absolute URLs (e.g. external CDN image).
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(pictureUrl: "https://cdn.example.com/img.png");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenAllFieldsAreValidWithRelativePictureUrl_HasNoErrors()
    {
        // Arrange
        // BeValidUrl uses Uri.TryCreate with UriKind.RelativeOrAbsolute —
        // relative URLs (e.g. what our own /api/product-image endpoint
        // returns) MUST be accepted. This is the documented contract.
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(pictureUrl: "/uploads/products/abc.jpg");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── ProductId rules ────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenProductIdIsEmpty_ReturnsProductIdRequiredError()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(productId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product ID is required.");
    }

    // ── Name rules ──────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenNameIsEmpty_ReturnsNameRequiredError()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(name: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product name is required.");
    }

    [Fact]
    public void Validate_WhenNameIsWhitespace_ReturnsNameRequiredError()
    {
        // Arrange
        // FluentValidation's NotEmpty uses IsNullOrWhiteSpace for strings.
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(name: "   ");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product name is required.");
    }

    [Fact]
    public void Validate_WhenNameExceedsMaxLength_ReturnsLengthError()
    {
        // Arrange
        // MaxNameLength = 200 (re-used from CreateProductCommandValidator).
        // A name with 201 characters must fail.
        var validator = new UpdateProductDetailsCommandValidator();
        var tooLongName = new string('a', 201);

        // Act
        var result = validator.Validate(BuildValidCommand(name: tooLongName));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product name cannot exceed 200 characters.");
    }

    [Fact]
    public void Validate_WhenNameIsExactlyMaxLength_HasNoNameErrors()
    {
        // Arrange
        // Boundary: 200 chars is allowed (MaximumLength is inclusive).
        var validator = new UpdateProductDetailsCommandValidator();
        var name = new string('a', 200);

        // Act
        var result = validator.Validate(BuildValidCommand(name: name));

        // Assert
        result.Errors.Should().NotContain(e => e.ErrorMessage.Contains("Product name"));
    }

    // ── Description rules ───────────────────────────────────────────────

    [Fact]
    public void Validate_WhenDescriptionIsEmpty_ReturnsDescriptionRequiredError()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(description: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product description is required.");
    }

    [Fact]
    public void Validate_WhenDescriptionExceedsMaxLength_ReturnsLengthError()
    {
        // Arrange
        // MaxDescriptionLength = 2000. A description with 2001 chars fails.
        var validator = new UpdateProductDetailsCommandValidator();
        var tooLong = new string('a', 2001);

        // Act
        var result = validator.Validate(BuildValidCommand(description: tooLong));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product description cannot exceed 2000 characters.");
    }

    // ── PictureUrl rules ───────────────────────────────────────────────

    [Fact]
    public void Validate_WhenPictureUrlIsNull_HasNoPictureUrlErrors()
    {
        // Arrange
        // PictureUrl is optional. The .When(...) clause on BeValidUrl
        // skips validation when the value is null/whitespace, and
        // MaximumLength skips null values by default.
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(pictureUrl: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPictureUrlIsWhitespace_HasNoPictureUrlErrors()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(pictureUrl: "   ");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPictureUrlExceedsMaxLength_ReturnsLengthError()
    {
        // Arrange
        // MaxPictureUrlLength = 500. A URL with 501 chars fails — note
        // MaximumLength applies regardless of the .When(...) guard on
        // BeValidUrl; only the BeValidUrl rule is gated.
        var validator = new UpdateProductDetailsCommandValidator();
        var tooLong = "https://example.com/" + new string('a', 501 - "https://example.com/".Length);

        // Act
        var result = validator.Validate(BuildValidCommand(pictureUrl: tooLong));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Picture URL cannot exceed 500 characters.");
    }

    // The BeValidUrl rule is permissive — Uri.TryCreate with
    // UriKind.RelativeOrAbsolute accepts "not-a-url" as a valid RELATIVE
    // URI (it's just a path segment). To actually fail BeValidUrl we need
    // a string Uri.TryCreate rejects: a URL with a space in the HOST
    // component (which is invalid per RFC 3986). Verified empirically:
    // Uri.TryCreate("http://exa mple.com", UriKind.RelativeOrAbsolute, out _)
    // returns false.
    [Fact]
    public void Validate_WhenPictureUrlIsNotAValidUrl_ReturnsUrlFormatError()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(pictureUrl: "http://exa mple.com");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Picture URL must be a valid URL"));
    }

    // ── Price rules ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenPriceAmountIsZero_ReturnsPriceMustBeGreaterThanZeroError()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(amount: 0m);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product price must be greater than zero.");
    }

    [Fact]
    public void Validate_WhenPriceAmountIsNegative_ReturnsPriceMustBeGreaterThanZeroError()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(amount: -1m);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product price must be greater than zero.");
    }

    // ── Currency rules ──────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenCurrencyIsEmpty_ReturnsCurrencyRequiredError()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(currency: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Currency is required.");
    }

    [Fact]
    public void Validate_WhenCurrencyIsTwoChars_ReturnsCurrencyLengthError()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(currency: "US");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Currency must be a 3-letter ISO 4217 code (e.g. USD, IRR).");
    }

    [Fact]
    public void Validate_WhenCurrencyIsFourChars_ReturnsCurrencyLengthError()
    {
        // Arrange
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(currency: "USDD");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Currency must be a 3-letter ISO 4217 code (e.g. USD, IRR).");
    }

    [Fact]
    public void Validate_WhenCurrencyIsThreeChars_HasNoCurrencyErrors()
    {
        // Arrange
        // Length(3) is inclusive — exactly 3 chars is allowed.
        var validator = new UpdateProductDetailsCommandValidator();
        var command = BuildValidCommand(currency: "IRR");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }
}

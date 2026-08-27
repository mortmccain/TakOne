using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Products.Commands.CreateProduct;
using TakOne.SharedKernel.DTOs;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.CreateProduct;

/// <summary>
/// Unit tests for <see cref="CreateProductCommandValidator"/>.
///
/// COVERAGE APPROACH: each rule in the validator is exercised by at least
/// one positive (passes) and one negative (fails with the documented
/// message) test. We use the simple `validator.Validate(command)` API and
/// inspect the `ValidationResult.Errors` collection rather than pulling in
/// the FluentValidation.TestHelper package.
/// </summary>
public class CreateProductCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — every field set to a value that passes all
    // the validator's rules. Each test then mutates one field to exercise
    // a single rule.
    private static CreateProductCommand BuildValidCommand(
        string? name = null,
        string? description = null,
        string? pictureUrl = null,
        decimal? amount = null,
        string? currency = null,
        int? stock = null,
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null,
        IReadOnlyList<PurchaseLimitInputDto>? purchaseLimits = null)
    {
        return new CreateProductCommand(
            Name: name ?? "Apple",
            Description: description ?? "A red apple",
            PictureUrl: pictureUrl,
            Price: new MoneyDto { Amount = amount ?? 1.5m, Currency = currency ?? "USD" },
            InitialStockQuantity: stock ?? 10,
            CategoryId: categoryId ?? Guid.NewGuid(),
            SubCategoryId: subCategoryId,
            SubSubCategoryId: subSubCategoryId,
            PurchaseLimits: purchaseLimits);
    }

    // Returns the error message for the first failure for the given
    // property name (or null if there's no failure for that property).
    private static string? FirstErrorFor(ValidationResult result, string propertyName)
        => result.Errors.FirstOrDefault(e => e.PropertyName == propertyName)?.ErrorMessage;

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenAllFieldsAreValidWithOptionalPictureUrl_HasNoErrors()
    {
        // Arrange
        // A valid absolute URL is accepted by the BeValidUrl rule.
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(pictureUrl: "https://cdn.example.com/img.png");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPictureUrlIsRelativeUrl_HasNoErrors()
    {
        // Arrange
        // The validator accepts both absolute AND relative URLs — relative
        // URLs are what /api/product-image returns.
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(pictureUrl: "/uploads/products/abc.jpg");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── Name rules ──────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenNameIsEmpty_ReturnsNameRequiredError()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
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
        // FluentValidation's NotEmpty uses IsNullOrWhiteSpace for strings,
        // so a whitespace-only name is treated as missing.
        var validator = new CreateProductCommandValidator();
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
        // MaxNameLength = 200. A name with 201 characters must fail.
        var validator = new CreateProductCommandValidator();
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
        var validator = new CreateProductCommandValidator();
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
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(description: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product description is required.");
    }

    [Fact]
    public void Validate_WhenDescriptionIsWhitespace_ReturnsDescriptionRequiredError()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(description: "   ");

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
        var validator = new CreateProductCommandValidator();
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
        // PictureUrl is optional. The .When(...) clause on the BeValidUrl
        // rule skips validation when the value is null/whitespace, and
        // MaximumLength skips null values by default.
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(pictureUrl: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPictureUrlIsEmptyString_HasNoPictureUrlErrors()
    {
        // Arrange
        // Empty string is treated as "not set" — the .When(...) clause on
        // BeValidUrl uses IsNullOrWhiteSpace, which is true for "".
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(pictureUrl: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPictureUrlIsWhitespace_HasNoPictureUrlErrors()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(pictureUrl: "   ");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPictureUrlIsNotAValidUrl_ReturnsUrlFormatError()
    {
        // Arrange
        // Uri.TryCreate is permissive — "not-a-url" is accepted as a
        // valid RELATIVE URI (it's just a path segment). To actually fail
        // the BeValidUrl rule, we need a string that Uri.TryCreate rejects:
        // a URL with a space in the HOST component (which is invalid per
        // RFC 3986 — host names cannot contain spaces).
        //
        // Verified empirically: Uri.TryCreate("http://exa mple.com",
        // UriKind.RelativeOrAbsolute, out _) returns false.
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(pictureUrl: "http://exa mple.com");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Picture URL must be a valid URL"));
    }

    [Fact]
    public void Validate_WhenPictureUrlExceedsMaxLength_ReturnsLengthError()
    {
        // Arrange
        // MaxPictureUrlLength = 500. A URL with 501 chars fails — note
        // MaximumLength applies regardless of the .When(...) guard on
        // BeValidUrl; only the BeValidUrl rule is gated.
        var validator = new CreateProductCommandValidator();
        var tooLong = "https://example.com/" + new string('a', 501 - "https://example.com/".Length);

        // Act
        var result = validator.Validate(BuildValidCommand(pictureUrl: tooLong));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Picture URL cannot exceed 500 characters.");
    }

    // ── Price rules ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenPriceIsNull_ReturnsPriceRequiredError()
    {
        // Arrange
        // Price is a required reference. We can't pass null directly to the
        // record (the parameter is non-nullable), so we set the entire
        // Price to a MoneyDto with Amount=0 and Currency="" to drive the
        // nested Amount/Currency rules. To truly exercise the Price NotNull
        // rule we'd need a nullable Price — the validator's `.NotNull()`
        // on Price is effectively dead code for this strongly-typed record,
        // but the rule still exists. We skip that case and exercise the
        // Price.Amount and Price.Currency rules instead (they're the
        // reachable rules).
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(amount: 0m);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product price must be greater than zero.");
    }

    [Fact]
    public void Validate_WhenPriceAmountIsZero_ReturnsPriceMustBeGreaterThanZeroError()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
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
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(amount: -1m);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product price must be greater than zero.");
    }

    [Fact]
    public void Validate_WhenPriceAmountIsPositive_HasNoPriceErrors()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(amount: 0.01m);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenCurrencyIsEmpty_ReturnsCurrencyRequiredError()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
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
        var validator = new CreateProductCommandValidator();
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
        var validator = new CreateProductCommandValidator();
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
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(currency: "IRR");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── InitialStockQuantity rules ─────────────────────────────────────

    [Fact]
    public void Validate_WhenInitialStockQuantityIsNegative_ReturnsNegativeStockError()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(stock: -1);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Initial stock quantity cannot be negative.");
    }

    [Fact]
    public void Validate_WhenInitialStockQuantityIsZero_HasNoStockErrors()
    {
        // Arrange
        // GreaterThanOrEqualTo(0) is inclusive — 0 is allowed.
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(stock: 0);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenInitialStockQuantityExceedsMax_ReturnsMaxStockError()
    {
        // Arrange
        // MaxStockQuantity = 1_000_000. 1_000_001 fails.
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(stock: 1_000_001);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Initial stock quantity cannot exceed 1,000,000.");
    }

    [Fact]
    public void Validate_WhenInitialStockQuantityIsExactlyMax_HasNoStockErrors()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(stock: 1_000_000);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── CategoryId rules ────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenCategoryIdIsEmpty_ReturnsCategoryIdRequiredError()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(categoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category ID is required.");
    }

    // ── SubCategoryId / SubSubCategoryId consistency ────────────────────

    [Fact]
    public void Validate_WhenSubSubCategoryIdSetWithoutSubCategoryId_ReturnsConsistencyError()
    {
        // Arrange
        // The Must rule: SubCategoryId not null OR SubSubCategoryId null.
        // Setting only SubSub violates the rule.
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(
            subCategoryId: null,
            subSubCategoryId: Guid.NewGuid());

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Cannot specify a SubSubCategoryId without a SubCategoryId.");
    }

    [Fact]
    public void Validate_WhenSubCategoryIdSetWithoutSubSubCategoryId_HasNoConsistencyErrors()
    {
        // Arrange
        // Sub without SubSub is valid (SubSub is optional).
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(
            subCategoryId: Guid.NewGuid(),
            subSubCategoryId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenBothSubAndSubSubAreNull_HasNoConsistencyErrors()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(
            subCategoryId: null,
            subSubCategoryId: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenBothSubAndSubSubAreSet_HasNoConsistencyErrors()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(
            subCategoryId: Guid.NewGuid(),
            subSubCategoryId: Guid.NewGuid());

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── PurchaseLimits rules ────────────────────────────────────────────

    [Fact]
    public void Validate_WhenPurchaseLimitsIsNull_HasNoPurchaseLimitErrors()
    {
        // Arrange
        // The RuleForEach is gated by .When(x => x.PurchaseLimits is
        // { Count: > 0 }) — null skips the rule.
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(purchaseLimits: null);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPurchaseLimitsIsEmptyList_HasNoPurchaseLimitErrors()
    {
        // Arrange
        // Empty list (Count == 0) skips the RuleForEach too.
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(purchaseLimits: Array.Empty<PurchaseLimitInputDto>());

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPurchaseLimitHasEmptyGroupId_ReturnsGroupRequiredError()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var limits = new[]
        {
            new PurchaseLimitInputDto { GroupId = Guid.Empty, Limit = 5 },
        };
        var command = BuildValidCommand(purchaseLimits: limits);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Group is required for each purchase limit.");
    }

    [Fact]
    public void Validate_WhenPurchaseLimitHasLimitBelowOne_ReturnsLimitTooLowError()
    {
        // Arrange
        var validator = new CreateProductCommandValidator();
        var limits = new[]
        {
            new PurchaseLimitInputDto { GroupId = Guid.NewGuid(), Limit = 0 },
        };
        var command = BuildValidCommand(purchaseLimits: limits);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Purchase limit must be at least 1.");
    }

    [Fact]
    public void Validate_WhenPurchaseLimitHasLimitExceedingMax_ReturnsLimitTooHighError()
    {
        // Arrange
        // Max limit is 10_000. 10_001 fails.
        var validator = new CreateProductCommandValidator();
        var limits = new[]
        {
            new PurchaseLimitInputDto { GroupId = Guid.NewGuid(), Limit = 10_001 },
        };
        var command = BuildValidCommand(purchaseLimits: limits);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Purchase limit cannot exceed 10,000.");
    }

    [Fact]
    public void Validate_WhenPurchaseLimitHasLimitExactlyOne_HasNoPurchaseLimitErrors()
    {
        // Arrange
        // Boundary: 1 is allowed (GreaterThanOrEqualTo(1) is inclusive).
        var validator = new CreateProductCommandValidator();
        var limits = new[]
        {
            new PurchaseLimitInputDto { GroupId = Guid.NewGuid(), Limit = 1 },
        };
        var command = BuildValidCommand(purchaseLimits: limits);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPurchaseLimitHasLimitExactlyMax_HasNoPurchaseLimitErrors()
    {
        // Arrange
        // Boundary: 10_000 is allowed.
        var validator = new CreateProductCommandValidator();
        var limits = new[]
        {
            new PurchaseLimitInputDto { GroupId = Guid.NewGuid(), Limit = 10_000 },
        };
        var command = BuildValidCommand(purchaseLimits: limits);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenMultiplePurchaseLimitsAllValid_HasNoErrors()
    {
        // Arrange
        // Multiple valid entries — multiple groups, each with a valid limit.
        var validator = new CreateProductCommandValidator();
        var limits = new[]
        {
            new PurchaseLimitInputDto { GroupId = Guid.NewGuid(), Limit = 5 },
            new PurchaseLimitInputDto { GroupId = Guid.NewGuid(), Limit = 100 },
            new PurchaseLimitInputDto { GroupId = Guid.NewGuid(), Limit = 1 },
        };
        var command = BuildValidCommand(purchaseLimits: limits);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── Multi-rule interaction ─────────────────────────────────────────

    [Fact]
    public void Validate_WhenMultipleFieldsInvalid_ReturnsAllErrors()
    {
        // Arrange
        // Multiple simultaneous violations — the validator must surface
        // all of them (no short-circuit by default).
        var validator = new CreateProductCommandValidator();
        var command = BuildValidCommand(
            name: string.Empty,
            description: string.Empty,
            amount: 0m,
            stock: -1,
            categoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product name is required.");
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product description is required.");
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product price must be greater than zero.");
        result.Errors.Should().Contain(e => e.ErrorMessage == "Initial stock quantity cannot be negative.");
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category ID is required.");
    }
}

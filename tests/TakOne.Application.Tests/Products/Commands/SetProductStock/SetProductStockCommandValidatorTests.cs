using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Products.Commands.SetProductStock;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.SetProductStock;

/// <summary>
/// Unit tests for <see cref="SetProductStockCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has three primitive rules —
/// ProductId NotEmpty, Quantity GreaterThan(0) with a "use deactivation
/// instead" hint, and Quantity LessThanOrEqualTo(MaxStockValue=100_000).
/// Each rule is exercised with both a passing boundary (e.g. Quantity=1
/// passes GreaterThan(0)) and a failing boundary (e.g. Quantity=0 fails
/// GreaterThan(0); Quantity=100_001 fails LessThanOrEqualTo).
/// </summary>
public class SetProductStockCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — Quantity=10 is comfortably inside the
    // [1, 100_000] band. Each test then mutates one field to exercise a
    // single rule.
    private static SetProductStockCommand BuildValidCommand(
        Guid? productId = null,
        int? quantity = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            Quantity: quantity ?? 10);

    private static string? FirstErrorFor(ValidationResult result, string propertyName)
        => result.Errors.FirstOrDefault(e => e.PropertyName == propertyName)?.ErrorMessage;

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new SetProductStockCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenQuantityIsOne_HasNoErrors()
    {
        // Arrange
        // GreaterThan(0) lower boundary is INCLUSIVE at 1 — the rule
        // rejects strictly-less-than-1 values, so 1 must pass.
        var validator = new SetProductStockCommandValidator();
        var command = BuildValidCommand(quantity: 1);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenQuantityIsMaxStockValue_HasNoErrors()
    {
        // Arrange
        // LessThanOrEqualTo(MaxStockValue) upper boundary is INCLUSIVE —
        // exactly MaxStockValue (100_000) must pass.
        var validator = new SetProductStockCommandValidator();
        var command = BuildValidCommand(quantity: SetProductStockCommandValidator.MaxStockValue);

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
        var validator = new SetProductStockCommandValidator();
        var command = BuildValidCommand(productId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product ID is required.");
    }

    // ── Quantity rules ─────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenQuantityIsZero_ReturnsGreaterThanZeroError()
    {
        // Arrange
        // 0 is the boundary case for GreaterThan(0) — must fail with the
        // hint that the caller should deactivate the product instead.
        var validator = new SetProductStockCommandValidator();
        var command = BuildValidCommand(quantity: 0);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage ==
            "Stock quantity must be greater than zero. To make it zero, deactivate the product instead.");
    }

    [Fact]
    public void Validate_WhenQuantityIsNegative_ReturnsGreaterThanZeroError()
    {
        // Arrange
        // A clearly-negative quantity also fails GreaterThan(0) — this
        // test exists to document that the rule isn't accidentally a
        // GreaterThanOrEqualTo(0) in disguise.
        var validator = new SetProductStockCommandValidator();
        var command = BuildValidCommand(quantity: -5);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage ==
            "Stock quantity must be greater than zero. To make it zero, deactivate the product instead.");
    }

    [Fact]
    public void Validate_WhenQuantityExceedsMaxStockValue_ReturnsMaxStockError()
    {
        // Arrange
        // MaxStockValue = 100_000. 100_001 fails LessThanOrEqualTo.
        var validator = new SetProductStockCommandValidator();
        var command = BuildValidCommand(quantity: SetProductStockCommandValidator.MaxStockValue + 1);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Stock quantity cannot exceed 100,000 units.");
    }

    // The GreaterThan(0) error message must point staff at the
    // deactivation flow — verifying the exact text here is what
    // protects the business rule ("zero is deactivation's job, not
    // SetProductStock's job") from being silently relaxed.
    [Fact]
    public void Validate_WhenQuantityIsZero_GreaterThanErrorMessageMentionsDeactivation()
    {
        // Arrange
        var validator = new SetProductStockCommandValidator();
        var command = BuildValidCommand(quantity: 0);

        // Act
        var result = validator.Validate(command);
        var quantityError = FirstErrorFor(result, "Quantity");

        // Assert
        quantityError.Should().NotBeNull();
        quantityError!.Should().Contain("deactivate the product");
    }
}

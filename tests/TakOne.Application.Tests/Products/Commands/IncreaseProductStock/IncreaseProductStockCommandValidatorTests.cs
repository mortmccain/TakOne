using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Products.Commands.IncreaseProductStock;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.IncreaseProductStock;

/// <summary>
/// Unit tests for <see cref="IncreaseProductStockCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has three primitive rules —
/// ProductId NotEmpty, Quantity GreaterThan(0) (restock quantity must be
/// positive), and Quantity LessThanOrEqualTo(MaxSingleIncrease=100_000)
/// with a "split large restocks" hint. Each rule is exercised with both
/// a passing boundary and a failing boundary.
/// </summary>
public class IncreaseProductStockCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static IncreaseProductStockCommand BuildValidCommand(
        Guid? productId = null,
        int? quantity = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            Quantity: quantity ?? 50);

    private static string? FirstErrorFor(ValidationResult result, string propertyName)
        => result.Errors.FirstOrDefault(e => e.PropertyName == propertyName)?.ErrorMessage;

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new IncreaseProductStockCommandValidator();
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
        // GreaterThan(0) boundary: 1 is the smallest valid restock.
        var validator = new IncreaseProductStockCommandValidator();
        var command = BuildValidCommand(quantity: 1);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenQuantityIsMaxSingleIncrease_HasNoErrors()
    {
        // Arrange
        // LessThanOrEqualTo(MaxSingleIncrease) boundary: exactly 100_000
        // is the largest valid single restock.
        var validator = new IncreaseProductStockCommandValidator();
        var command = BuildValidCommand(quantity: IncreaseProductStockCommandValidator.MaxSingleIncrease);

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
        var validator = new IncreaseProductStockCommandValidator();
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
        // 0 is the boundary case for GreaterThan(0) — must fail because a
        // restock of zero is a no-op (caller shouldn't dispatch a command
        // for a no-op).
        var validator = new IncreaseProductStockCommandValidator();
        var command = BuildValidCommand(quantity: 0);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Stock increase quantity must be greater than zero.");
    }

    [Fact]
    public void Validate_WhenQuantityIsNegative_ReturnsGreaterThanZeroError()
    {
        // Arrange
        // A clearly-negative quantity also fails GreaterThan(0) — this
        // documents that IncreaseProductStock is NOT for decreasing stock.
        var validator = new IncreaseProductStockCommandValidator();
        var command = BuildValidCommand(quantity: -10);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Stock increase quantity must be greater than zero.");
    }

    [Fact]
    public void Validate_WhenQuantityExceedsMaxSingleIncrease_ReturnsMaxSingleIncreaseError()
    {
        // Arrange
        // MaxSingleIncrease = 100_000. 100_001 fails LessThanOrEqualTo.
        var validator = new IncreaseProductStockCommandValidator();
        var command = BuildValidCommand(quantity: IncreaseProductStockCommandValidator.MaxSingleIncrease + 1);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage ==
            "A single stock increase cannot exceed 100,000 units. Split large restocks into multiple commands.");
    }

    // The LessThanOrEqualTo error message must point staff at the
    // "split large restocks" workflow — verifying the exact text here
    // protects the "no single restock > 100K" sanity cap from being
    // silently relaxed (a typo entering 1,000,000 instead of 100 would
    // otherwise blow up the catalog).
    [Fact]
    public void Validate_WhenQuantityExceedsMaxSingleIncrease_ErrorMessageMentionsSplitting()
    {
        // Arrange
        var validator = new IncreaseProductStockCommandValidator();
        var command = BuildValidCommand(quantity: IncreaseProductStockCommandValidator.MaxSingleIncrease + 1);

        // Act
        var result = validator.Validate(command);
        var quantityError = FirstErrorFor(result, "Quantity");

        // Assert
        quantityError.Should().NotBeNull();
        quantityError!.Should().Contain("Split large restocks");
    }
}

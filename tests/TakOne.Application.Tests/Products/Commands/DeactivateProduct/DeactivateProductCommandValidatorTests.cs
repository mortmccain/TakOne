using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Products.Commands.DeactivateProduct;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.DeactivateProduct;

/// <summary>
/// Unit tests for <see cref="DeactivateProductCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has only ONE primitive rule —
/// ProductId NotEmpty. There's no stock value to validate (the command
/// unconditionally sets stock to 0, and the domain's SetStock(0) is
/// always valid). We cover the happy path, the empty-Guid rejection,
/// and one boundary case (a random Guid) to document the rule's intent.
/// </summary>
public class DeactivateProductCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static DeactivateProductCommand BuildValidCommand(Guid? productId = null)
        => new(productId ?? TestValues.ProductId);

    private static string? FirstErrorFor(ValidationResult result, string propertyName)
        => result.Errors.FirstOrDefault(e => e.PropertyName == propertyName)?.ErrorMessage;

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenProductIdIsNonEmpty_HasNoErrors()
    {
        // Arrange
        var validator = new DeactivateProductCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // ── ProductId rule ──────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenProductIdIsEmpty_ReturnsProductIdRequiredError()
    {
        // Arrange
        // Guid.Empty is the only value NotEmpty rejects — the rule is
        // trivial but it's defense-in-depth against a caller that
        // constructs the command without setting the ProductId.
        var validator = new DeactivateProductCommandValidator();
        var command = BuildValidCommand(productId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle();
        result.Errors.Single().PropertyName.Should().Be("ProductId");
        result.Errors.Single().ErrorMessage.Should().Be("Product ID is required.");
    }

    // Confirm the exact error message text — the spec mandates
    // "Product ID is required." (with the period). A refactor that
    // accidentally drops the period or rephrases the message would
    // be caught here.
    [Fact]
    public void Validate_WhenProductIdIsEmpty_ErrorIsOnProductIdPropertyWithExactMessage()
    {
        // Arrange
        var validator = new DeactivateProductCommandValidator();
        var command = BuildValidCommand(productId: Guid.Empty);

        // Act
        var result = validator.Validate(command);
        var productIdError = FirstErrorFor(result, "ProductId");

        // Assert
        productIdError.Should().NotBeNull();
        productIdError.Should().Be("Product ID is required.");
    }
}

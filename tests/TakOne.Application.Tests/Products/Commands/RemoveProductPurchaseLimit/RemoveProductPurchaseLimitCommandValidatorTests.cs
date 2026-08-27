using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Products.Commands.RemoveProductPurchaseLimit;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.RemoveProductPurchaseLimit;

/// <summary>
/// Unit tests for <see cref="RemoveProductPurchaseLimitCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has only two primitive rules —
/// ProductId NotEmpty and GroupId NotEmpty. (There's no Limit field
/// to validate — removal is unconditional.) Each rule is exercised
/// with the empty-Guid boundary. A "happy path" test confirms the
/// valid case has zero errors.
/// </summary>
public class RemoveProductPurchaseLimitCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static RemoveProductPurchaseLimitCommand BuildValidCommand(
        Guid? productId = null,
        Guid? groupId = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            GroupId: groupId ?? TestValues.GroupId);

    private static string? FirstErrorFor(ValidationResult result, string propertyName)
        => result.Errors.FirstOrDefault(e => e.PropertyName == propertyName)?.ErrorMessage;

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new RemoveProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // ── ProductId rules ────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenProductIdIsEmpty_ReturnsProductIdRequiredError()
    {
        // Arrange
        var validator = new RemoveProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(productId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product ID is required.");
    }

    // ── GroupId rules ──────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenGroupIdIsEmpty_ReturnsGroupIdRequiredError()
    {
        // Arrange
        var validator = new RemoveProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(groupId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Group ID is required.");
    }

    // Both fields empty at once must produce TWO errors (one per
    // property) — verifying this protects against a refactor that
    // accidentally collapses the two rules into one (e.g. using a
    // single Must rule on the whole command).
    [Fact]
    public void Validate_WhenBothProductIdAndGroupIdAreEmpty_ReturnsBothErrors()
    {
        // Arrange
        var validator = new RemoveProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(productId: Guid.Empty, groupId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().HaveCount(2);
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product ID is required.");
        result.Errors.Should().Contain(e => e.ErrorMessage == "Group ID is required.");
    }

    // Confirm the property name on each NotEmpty failure — FluentValidation
    // infers property names from the lambda body. If a refactor breaks
    // the inference (e.g. wraps the lambda in a method group), the error
    // would land on the wrong property name and break client-side field
    // highlighting.
    [Fact]
    public void Validate_WhenProductIdIsEmpty_ErrorIsOnTheProductIdProperty()
    {
        // Arrange
        var validator = new RemoveProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(productId: Guid.Empty);

        // Act
        var result = validator.Validate(command);
        var productIdError = FirstErrorFor(result, "ProductId");

        // Assert
        productIdError.Should().NotBeNull();
        productIdError.Should().Be("Product ID is required.");
    }
}

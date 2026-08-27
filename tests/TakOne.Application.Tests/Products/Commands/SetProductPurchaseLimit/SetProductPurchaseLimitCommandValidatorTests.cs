using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Products.Commands.SetProductPurchaseLimit;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.SetProductPurchaseLimit;

/// <summary>
/// Unit tests for <see cref="SetProductPurchaseLimitCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has four primitive rules —
/// ProductId NotEmpty, GroupId NotEmpty, Limit GreaterThanOrEqualTo(1)
/// (limit must be at least 1 — passing 0 would mean "no limit", which
/// is what RemoveProductPurchaseLimit is for), and Limit
/// LessThanOrEqualTo(MaxLimit=1_000_000). Each rule is exercised with
/// both passing and failing boundaries.
/// </summary>
public class SetProductPurchaseLimitCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static SetProductPurchaseLimitCommand BuildValidCommand(
        Guid? productId = null,
        Guid? groupId = null,
        int? limit = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            GroupId: groupId ?? TestValues.GroupId,
            Limit: limit ?? 5);

    private static string? FirstErrorFor(ValidationResult result, string propertyName)
        => result.Errors.FirstOrDefault(e => e.PropertyName == propertyName)?.ErrorMessage;

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new SetProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenLimitIsOne_HasNoErrors()
    {
        // Arrange
        // GreaterThanOrEqualTo(1) lower boundary is INCLUSIVE —
        // 1 is the smallest valid limit.
        var validator = new SetProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(limit: 1);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenLimitIsMaxLimit_HasNoErrors()
    {
        // Arrange
        // LessThanOrEqualTo(MaxLimit) upper boundary is INCLUSIVE —
        // exactly MaxLimit (1_000_000) is valid.
        var validator = new SetProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(limit: SetProductPurchaseLimitCommandValidator.MaxLimit);

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
        var validator = new SetProductPurchaseLimitCommandValidator();
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
        var validator = new SetProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(groupId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Group ID is required.");
    }

    // ── Limit rules ────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenLimitIsZero_ReturnsLimitMustBeAtLeastOneError()
    {
        // Arrange
        // 0 fails GreaterThanOrEqualTo(1) — a limit of 0 is not a
        // valid "limit value"; if the caller wants to remove the limit
        // they must use RemoveProductPurchaseLimitCommand.
        var validator = new SetProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(limit: 0);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Purchase limit must be at least 1.");
    }

    [Fact]
    public void Validate_WhenLimitIsNegative_ReturnsLimitMustBeAtLeastOneError()
    {
        // Arrange
        var validator = new SetProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(limit: -3);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Purchase limit must be at least 1.");
    }

    [Fact]
    public void Validate_WhenLimitExceedsMaxLimit_ReturnsMaxLimitError()
    {
        // Arrange
        // MaxLimit = 1_000_000. 1_000_001 fails LessThanOrEqualTo.
        var validator = new SetProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(limit: SetProductPurchaseLimitCommandValidator.MaxLimit + 1);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Purchase limit cannot exceed 1,000,000.");
    }

    // The exact MaxLimit message must contain the formatted number —
    // if someone refactors MaxLimit but forgets to update the message
    // template, the contract test fails. We assert the rendered number
    // explicitly so a typo (e.g. "1,000,000" vs "1000000") is caught.
    [Fact]
    public void Validate_WhenLimitExceedsMaxLimit_ErrorMessageContainsFormattedMax()
    {
        // Arrange
        var validator = new SetProductPurchaseLimitCommandValidator();
        var command = BuildValidCommand(limit: SetProductPurchaseLimitCommandValidator.MaxLimit + 1);

        // Act
        var result = validator.Validate(command);
        var limitError = FirstErrorFor(result, "Limit");

        // Assert
        limitError.Should().NotBeNull();
        limitError!.Should().Contain("1,000,000");
    }
}

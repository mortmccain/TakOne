using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Categories.Commands.ActivateCategory;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.ActivateCategory;

/// <summary>
/// Unit tests for <see cref="ActivateCategoryCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has a single NotEmpty rule on
/// CategoryId. We exercise the happy path plus the empty-Guid failure
/// case, and assert the exact error message text that the SUT emits
/// (locking in the contract for the UI's localization layer).
/// </summary>
public class ActivateCategoryCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — the stable TestValues.CategoryId is a
    // well-known non-empty Guid. Each test then mutates one field to
    // exercise a single rule.
    private static ActivateCategoryCommand BuildValidCommand(Guid? categoryId = null)
        => new(categoryId ?? TestValues.CategoryId);

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenCategoryIdIsNonEmpty_HasNoErrors()
    {
        // Arrange
        var validator = new ActivateCategoryCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // ── CategoryId NotEmpty rule ───────────────────────────────────────

    [Fact]
    public void Validate_WhenCategoryIdIsEmpty_ReturnsCategoryIdRequiredError()
    {
        // Arrange
        // Guid.Empty is the only value NotEmpty rejects for Guid properties.
        var validator = new ActivateCategoryCommandValidator();
        var command = BuildValidCommand(categoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category ID is required.");
    }

    // ── Property name on failure ───────────────────────────────────────

    // Locking in the property name "CategoryId" on the failure ensures
    // the UI's per-field error highlighting can rely on the property
    // name staying stable across refactors.
    [Fact]
    public void Validate_WhenCategoryIdIsEmpty_FailureTargetsCategoryIdProperty()
    {
        // Arrange
        var validator = new ActivateCategoryCommandValidator();
        var command = BuildValidCommand(categoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ActivateCategoryCommand.CategoryId));
    }
}

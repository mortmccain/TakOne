using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Categories.Commands.SubSubCategoryCommands.DeactivateSubSubCategory;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.SubSubCategoryCommands.DeactivateSubSubCategory;

/// <summary>
/// Unit tests for <see cref="DeactivateSubSubCategoryCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has three NotEmpty rules — CategoryId,
/// SubCategoryId, and SubSubCategoryId. Same shape as the Activate
/// validator — we exercise the happy path plus the empty-Guid failure
/// cases for each of the three Guid properties.
/// </summary>
public class DeactivateSubSubCategoryCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static DeactivateSubSubCategoryCommand BuildValidCommand(
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null)
        => new(
            categoryId ?? TestValues.CategoryId,
            subCategoryId ?? TestValues.SubCategoryId,
            subSubCategoryId ?? TestValues.SubSubCategoryId);

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new DeactivateSubSubCategoryCommandValidator();
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
        var validator = new DeactivateSubSubCategoryCommandValidator();
        var command = BuildValidCommand(categoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category ID is required.");
    }

    // ── SubCategoryId NotEmpty rule ─────────────────────────────────────

    [Fact]
    public void Validate_WhenSubCategoryIdIsEmpty_ReturnsSubCategoryIdRequiredError()
    {
        // Arrange
        var validator = new DeactivateSubSubCategoryCommandValidator();
        var command = BuildValidCommand(subCategoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubCategory ID is required.");
    }

    // ── SubSubCategoryId NotEmpty rule ──────────────────────────────────

    [Fact]
    public void Validate_WhenSubSubCategoryIdIsEmpty_ReturnsSubSubCategoryIdRequiredError()
    {
        // Arrange
        var validator = new DeactivateSubSubCategoryCommandValidator();
        var command = BuildValidCommand(subSubCategoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubSubCategory ID is required.");
    }

    // ── All three empty (multi-error interaction) ──────────────────────

    [Fact]
    public void Validate_WhenAllThreeIdsAreEmpty_ReturnsAllThreeErrors()
    {
        // Arrange
        var validator = new DeactivateSubSubCategoryCommandValidator();
        var command = BuildValidCommand(
            categoryId: Guid.Empty,
            subCategoryId: Guid.Empty,
            subSubCategoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category ID is required.");
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubCategory ID is required.");
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubSubCategory ID is required.");
    }
}

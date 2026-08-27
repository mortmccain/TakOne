using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Categories.Commands.SubSubCategoryCommands.RenameSubSubCategory;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.SubSubCategoryCommands.RenameSubSubCategory;

/// <summary>
/// Unit tests for <see cref="RenameSubSubCategoryCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has four primitive rules — CategoryId
/// NotEmpty, SubCategoryId NotEmpty, SubSubCategoryId NotEmpty, NewName
/// NotEmpty + MaximumLength(MaxNameLength=100). We exercise the happy
/// path plus each individual rule's failure case.
///
/// The validator does NOT check sibling-name uniqueness within the
/// parent SubCategory — that's the aggregate's responsibility (it
/// excludes the renamed entity's own Id from the candidate-collision set).
/// </summary>
public class RenameSubSubCategoryCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static RenameSubSubCategoryCommand BuildValidCommand(
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null,
        string? newName = null)
        => new(
            categoryId ?? TestValues.CategoryId,
            subCategoryId ?? TestValues.SubCategoryId,
            subSubCategoryId ?? TestValues.SubSubCategoryId,
            newName ?? "Renamed SubSub");

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new RenameSubSubCategoryCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenNewNameIsExactlyMaxLength_HasNoErrors()
    {
        // Arrange
        // MaximumLength(100) boundary is INCLUSIVE at 100.
        var validator = new RenameSubSubCategoryCommandValidator();
        var name = new string('a', 100);

        // Act
        var result = validator.Validate(BuildValidCommand(newName: name));

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── CategoryId NotEmpty rule ───────────────────────────────────────

    [Fact]
    public void Validate_WhenCategoryIdIsEmpty_ReturnsCategoryIdRequiredError()
    {
        // Arrange
        var validator = new RenameSubSubCategoryCommandValidator();
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
        var validator = new RenameSubSubCategoryCommandValidator();
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
        var validator = new RenameSubSubCategoryCommandValidator();
        var command = BuildValidCommand(subSubCategoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubSubCategory ID is required.");
    }

    // ── NewName NotEmpty rule ──────────────────────────────────────────

    [Fact]
    public void Validate_WhenNewNameIsEmpty_ReturnsNewNameRequiredError()
    {
        // Arrange
        var validator = new RenameSubSubCategoryCommandValidator();
        var command = BuildValidCommand(newName: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "New SubSubCategory name is required.");
    }

    [Fact]
    public void Validate_WhenNewNameIsWhitespace_ReturnsNewNameRequiredError()
    {
        // Arrange
        // FluentValidation's NotEmpty uses IsNullOrWhiteSpace for strings.
        var validator = new RenameSubSubCategoryCommandValidator();
        var command = BuildValidCommand(newName: "   ");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "New SubSubCategory name is required.");
    }

    // ── NewName MaximumLength rule ─────────────────────────────────────

    [Fact]
    public void Validate_WhenNewNameExceedsMaxLength_ReturnsLengthError()
    {
        // Arrange
        // 101 chars fails MaximumLength(100).
        var validator = new RenameSubSubCategoryCommandValidator();
        var name = new string('a', 101);

        // Act
        var result = validator.Validate(BuildValidCommand(newName: name));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubSubCategory name cannot exceed 100 characters.");
    }

    // ── MaxNameLength constant ──────────────────────────────────────────

    [Fact]
    public void MaxNameLength_WhenRead_Is100()
    {
        // Arrange
        // (no setup — pure constant read)

        // Act
        var max = RenameSubSubCategoryCommandValidator.MaxNameLength;

        // Assert
        max.Should().Be(100);
    }
}

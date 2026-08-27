using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Categories.Commands.SubSubCategoryCommands.CreateSubSubCategory;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.SubSubCategoryCommands.CreateSubSubCategory;

/// <summary>
/// Unit tests for <see cref="CreateSubSubCategoryCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has three primitive rules —
/// CategoryId NotEmpty, SubCategoryId NotEmpty, Name NotEmpty +
/// MaximumLength(MaxNameLength=100). Each rule is exercised with both
/// a passing boundary and a failing boundary.
///
/// The validator does NOT check cross-aggregate invariants (whether the
/// SubCategory belongs to the Category, whether the parent is active,
/// sibling name uniqueness) — those are the aggregate's responsibility.
/// </summary>
public class CreateSubSubCategoryCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static CreateSubSubCategoryCommand BuildValidCommand(
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        string? name = null)
        => new(
            categoryId ?? TestValues.CategoryId,
            subCategoryId ?? TestValues.SubCategoryId,
            name ?? "New SubSub");

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new CreateSubSubCategoryCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenNameIsExactlyMaxLength_HasNoErrors()
    {
        // Arrange
        // MaximumLength(100) boundary is INCLUSIVE at 100.
        var validator = new CreateSubSubCategoryCommandValidator();
        var name = new string('a', 100);

        // Act
        var result = validator.Validate(BuildValidCommand(name: name));

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── CategoryId NotEmpty rule ───────────────────────────────────────

    [Fact]
    public void Validate_WhenCategoryIdIsEmpty_ReturnsCategoryIdRequiredError()
    {
        // Arrange
        var validator = new CreateSubSubCategoryCommandValidator();
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
        var validator = new CreateSubSubCategoryCommandValidator();
        var command = BuildValidCommand(subCategoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubCategory ID is required.");
    }

    // ── Name NotEmpty rule ──────────────────────────────────────────────

    [Fact]
    public void Validate_WhenNameIsEmpty_ReturnsNameRequiredError()
    {
        // Arrange
        var validator = new CreateSubSubCategoryCommandValidator();
        var command = BuildValidCommand(name: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubSubCategory name is required.");
    }

    [Fact]
    public void Validate_WhenNameIsWhitespace_ReturnsNameRequiredError()
    {
        // Arrange
        // FluentValidation's NotEmpty uses IsNullOrWhiteSpace for strings.
        var validator = new CreateSubSubCategoryCommandValidator();
        var command = BuildValidCommand(name: "   ");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubSubCategory name is required.");
    }

    // ── Name MaximumLength rule ─────────────────────────────────────────

    [Fact]
    public void Validate_WhenNameExceedsMaxLength_ReturnsLengthError()
    {
        // Arrange
        // 101 chars fails MaximumLength(100).
        var validator = new CreateSubSubCategoryCommandValidator();
        var name = new string('a', 101);

        // Act
        var result = validator.Validate(BuildValidCommand(name: name));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "SubSubCategory name cannot exceed 100 characters.");
    }

    [Fact]
    public void Validate_WhenNameFarExceedsMaxLength_ReturnsSameLengthError()
    {
        // Arrange
        // Well over the limit — same error message.
        var validator = new CreateSubSubCategoryCommandValidator();
        var name = new string('a', 1000);

        // Act
        var result = validator.Validate(BuildValidCommand(name: name));

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
        var max = CreateSubSubCategoryCommandValidator.MaxNameLength;

        // Assert
        max.Should().Be(100);
    }
}

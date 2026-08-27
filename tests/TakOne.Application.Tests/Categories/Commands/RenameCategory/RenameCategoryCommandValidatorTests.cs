using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Categories.Commands.RenameCategory;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.RenameCategory;

/// <summary>
/// Unit tests for <see cref="RenameCategoryCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has two primitive rules — CategoryId
/// NotEmpty, and NewName NotEmpty + MaximumLength(MaxNameLength=100).
/// Each rule is exercised with both a passing boundary (e.g. NewName
/// of exactly 100 chars passes) and a failing boundary (e.g. NewName
/// of 101 chars fails).
///
/// The validator does NOT check uniqueness against other categories —
/// that's the handler's responsibility (via
/// <c>categoryRepository.NameExistsAsync</c> with the renamed
/// category's own Id excluded).
/// </summary>
public class RenameCategoryCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — NewName="Renamed" is well inside the
    // [1, 100] length band, and CategoryId references the stable
    // TestValues.CategoryId.
    private static RenameCategoryCommand BuildValidCommand(
        Guid? categoryId = null,
        string? newName = null)
        => new(
            CategoryId: categoryId ?? TestValues.CategoryId,
            NewName: newName ?? "Renamed");

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenAllFieldsAreValid_HasNoErrors()
    {
        // Arrange
        var validator = new RenameCategoryCommandValidator();
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
        // MaximumLength(100) boundary is INCLUSIVE at 100 — exactly 100
        // chars passes.
        var validator = new RenameCategoryCommandValidator();
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
        var validator = new RenameCategoryCommandValidator();
        var command = BuildValidCommand(categoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category ID is required.");
    }

    // ── NewName NotEmpty rule ──────────────────────────────────────────

    [Fact]
    public void Validate_WhenNewNameIsEmpty_ReturnsNewNameRequiredError()
    {
        // Arrange
        var validator = new RenameCategoryCommandValidator();
        var command = BuildValidCommand(newName: string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "New category name is required.");
    }

    [Fact]
    public void Validate_WhenNewNameIsWhitespace_ReturnsNewNameRequiredError()
    {
        // Arrange
        // FluentValidation's NotEmpty uses IsNullOrWhiteSpace for
        // strings — whitespace-only is treated as empty.
        var validator = new RenameCategoryCommandValidator();
        var command = BuildValidCommand(newName: "   ");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "New category name is required.");
    }

    // ── NewName MaximumLength rule ──────────────────────────────────────

    [Fact]
    public void Validate_WhenNewNameExceedsMaxLength_ReturnsLengthError()
    {
        // Arrange
        // 101 chars fails MaximumLength(100).
        var validator = new RenameCategoryCommandValidator();
        var name = new string('a', 101);

        // Act
        var result = validator.Validate(BuildValidCommand(newName: name));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category name cannot exceed 100 characters.");
    }

    [Fact]
    public void Validate_WhenNewNameFarExceedsMaxLength_ReturnsSameLengthError()
    {
        // Arrange
        // Well over the limit — same error message.
        var validator = new RenameCategoryCommandValidator();
        var name = new string('a', 1000);

        // Act
        var result = validator.Validate(BuildValidCommand(newName: name));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category name cannot exceed 100 characters.");
    }

    // ── MaxNameLength constant ──────────────────────────────────────────

    [Fact]
    public void MaxNameLength_WhenRead_Is100()
    {
        // Arrange
        // (no setup — pure constant read)

        // Act
        var max = RenameCategoryCommandValidator.MaxNameLength;

        // Assert
        max.Should().Be(100);
    }
}

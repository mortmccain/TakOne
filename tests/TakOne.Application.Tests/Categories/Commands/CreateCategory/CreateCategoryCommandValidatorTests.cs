using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Categories.Commands.CreateCategory;
using Xunit;

namespace TakOne.Application.Tests.Categories.Commands.CreateCategory;

/// <summary>
/// Unit tests for <see cref="CreateCategoryCommandValidator"/>.
///
/// The validator only checks primitive, self-contained properties (Name
/// is the only field on the command) — it does NOT touch the database.
/// The cross-aggregate name-uniqueness check is the handler's
/// responsibility (via ICategoryRepository.NameExistsAsync).
/// </summary>
public class CreateCategoryCommandValidatorTests
{
    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenNameIsSimpleAscii_HasNoErrors()
    {
        // Arrange
        var validator = new CreateCategoryCommandValidator();
        var command = new CreateCategoryCommand("Books");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenNameIsPersian_HasNoErrors()
    {
        // Arrange
        // The validator does NOT restrict Unicode characters — Persian
        // category names are valid.
        var validator = new CreateCategoryCommandValidator();
        var command = new CreateCategoryCommand("کتاب‌ها");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenNameIsExactlyMaxLength_HasNoErrors()
    {
        // Arrange
        // MaxNameLength = 100; 100 chars is allowed (inclusive).
        var validator = new CreateCategoryCommandValidator();
        var name = new string('a', 100);

        // Act
        var result = validator.Validate(new CreateCategoryCommand(name));

        // Assert
        result.IsValid.Should().BeTrue();
    }

    // ── Name NotEmpty rule ──────────────────────────────────────────────

    [Fact]
    public void Validate_WhenNameIsEmpty_ReturnsNameRequiredError()
    {
        // Arrange
        var validator = new CreateCategoryCommandValidator();
        var command = new CreateCategoryCommand(string.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category name is required.");
    }

    [Fact]
    public void Validate_WhenNameIsWhitespace_ReturnsNameRequiredError()
    {
        // Arrange
        // FluentValidation's NotEmpty uses IsNullOrWhiteSpace for strings.
        var validator = new CreateCategoryCommandValidator();
        var command = new CreateCategoryCommand("   ");

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category name is required.");
    }

    // ── Name MaximumLength rule ──────────────────────────────────────────

    [Fact]
    public void Validate_WhenNameExceedsMaxLength_ReturnsLengthError()
    {
        // Arrange
        // 101 chars must fail (MaxNameLength = 100).
        var validator = new CreateCategoryCommandValidator();
        var name = new string('a', 101);

        // Act
        var result = validator.Validate(new CreateCategoryCommand(name));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category name cannot exceed 100 characters.");
    }

    [Fact]
    public void Validate_WhenNameFarExceedsMaxLength_ReturnsLengthError()
    {
        // Arrange
        // Well over the limit — same error message.
        var validator = new CreateCategoryCommandValidator();
        var name = new string('a', 1000);

        // Act
        var result = validator.Validate(new CreateCategoryCommand(name));

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category name cannot exceed 100 characters.");
    }

    // ── MaxNameLength constant ───────────────────────────────────────────

    [Fact]
    public void MaxNameLength_WhenRead_Is100()
    {
        // Arrange

        // Act
        var max = CreateCategoryCommandValidator.MaxNameLength;

        // Assert
        max.Should().Be(100);
    }

    // ── Multi-error interaction ─────────────────────────────────────────

    [Fact]
    public void Validate_WhenNameIsNullAndTooLong_ProducesAtLeastOneError()
    {
        // Arrange
        // The C# record's `Name` parameter is non-nullable, so passing null
        // is technically a compile-time warning. At runtime, the validator
        // must surface at least the NotEmpty error (and MaximumLength would
        // also short-circuit null by default). We assert that the
        // validator rejects the input.
        var validator = new CreateCategoryCommandValidator();
#pragma warning disable CS8600 // Converting null literal to possible non-null type — intentional for this test.
        var command = new CreateCategoryCommand(null!);
#pragma warning restore CS8600

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category name is required.");
    }
}

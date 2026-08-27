using FluentAssertions;
using FluentValidation.Results;
using TakOne.Application.Products.Commands.UpdateProductCategory;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Products.Commands.UpdateProductCategory;

/// <summary>
/// Unit tests for <see cref="UpdateProductCategoryCommandValidator"/>.
///
/// COVERAGE APPROACH: the validator has four primitive rules —
/// ProductId NotEmpty, CategoryId NotEmpty, and a self-contained
/// <c>Must(x => x.SubCategoryId is not null || x.SubSubCategoryId is null)</c>
/// rule that enforces "SubSub requires Sub". The cross-aggregate check
/// (Sub actually belongs to Category) is the handler's job and is NOT
/// tested here — that's in the handler tests file. Each rule is
/// exercised with both passing and failing cases.
/// </summary>
public class UpdateProductCategoryCommandValidatorTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    // Builds a valid command — only top-level CategoryId set, SubCategoryId
    // and SubSubCategoryId both null. This is the minimal happy path;
    // tests for the sub-branch add one or both nullable fields.
    private static UpdateProductCategoryCommand BuildValidCommand(
        Guid? productId = null,
        Guid? categoryId = null,
        Guid? subCategoryId = null,
        Guid? subSubCategoryId = null)
        => new(
            ProductId: productId ?? TestValues.ProductId,
            CategoryId: categoryId ?? TestValues.CategoryId,
            SubCategoryId: subCategoryId,
            SubSubCategoryId: subSubCategoryId);

    private static string? FirstErrorFor(ValidationResult result, string propertyName)
        => result.Errors.FirstOrDefault(e => e.PropertyName == propertyName)?.ErrorMessage;

    // ── Happy path ─────────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenOnlyCategoryIdIsSet_HasNoErrors()
    {
        // Arrange
        // SubCategoryId=null and SubSubCategoryId=null — both null is the
        // "move to top-level category only" case, and the Must rule is
        // satisfied (SubCategoryId is null OR SubSubCategoryId is null).
        var validator = new UpdateProductCategoryCommandValidator();
        var command = BuildValidCommand();

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenCategoryIdAndSubCategoryIdAreSet_HasNoErrors()
    {
        // Arrange
        // Sub without SubSub is a valid configuration (SubSub is optional).
        var validator = new UpdateProductCategoryCommandValidator();
        var command = BuildValidCommand(subCategoryId: TestValues.SubCategoryId);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenAllThreeLevelsAreSet_HasNoErrors()
    {
        // Arrange
        // Full hierarchy: Category + Sub + SubSub. The Must rule is
        // satisfied (SubCategoryId is not null — first clause of OR is true).
        var validator = new UpdateProductCategoryCommandValidator();
        var command = BuildValidCommand(
            subCategoryId: TestValues.SubCategoryId,
            subSubCategoryId: TestValues.SubSubCategoryId);

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
        var validator = new UpdateProductCategoryCommandValidator();
        var command = BuildValidCommand(productId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Product ID is required.");
    }

    // ── CategoryId rules ────────────────────────────────────────────────

    [Fact]
    public void Validate_WhenCategoryIdIsEmpty_ReturnsCategoryIdRequiredError()
    {
        // Arrange
        var validator = new UpdateProductCategoryCommandValidator();
        var command = BuildValidCommand(categoryId: Guid.Empty);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "Category ID is required.");
    }

    // ── SubSubCategoryId-without-SubCategoryId rule ─────────────────────

    [Fact]
    public void Validate_WhenSubSubCategoryIdIsSetWithoutSubCategoryId_ReturnsConsistencyError()
    {
        // Arrange
        // The Must rule: SubCategoryId not null OR SubSubCategoryId null.
        // Setting only SubSub violates the rule (and the Product
        // aggregate's EnsureSubCategoryConsistency would throw too).
        var validator = new UpdateProductCategoryCommandValidator();
        var command = BuildValidCommand(
            subCategoryId: null,
            subSubCategoryId: TestValues.SubSubCategoryId);

        // Act
        var result = validator.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage ==
            "Cannot specify a SubSubCategoryId without a SubCategoryId.");
    }

    // The Must rule lands on a property name FluentValidation infers from
    // the lambda body. The lambda is `RuleFor(x => x)` (the whole
    // command), so the property name is empty string "" — the rule
    // applies to the whole command, not to a single field. We assert
    // the error exists with that exact message regardless of property
    // name, which is what the UI surfaces to the user.
    [Fact]
    public void Validate_WhenSubSubWithoutSub_ErrorTextIsTheDocumentedMessage()
    {
        // Arrange
        var validator = new UpdateProductCategoryCommandValidator();
        var command = BuildValidCommand(
            subCategoryId: null,
            subSubCategoryId: TestValues.SubSubCategoryId);

        // Act
        var result = validator.Validate(command);
        var consistencyError = result.Errors
            .FirstOrDefault(e => e.ErrorMessage.Contains("SubSubCategoryId"));

        // Assert
        consistencyError.Should().NotBeNull();
        consistencyError!.ErrorMessage.Should().Be(
            "Cannot specify a SubSubCategoryId without a SubCategoryId.");
    }
}

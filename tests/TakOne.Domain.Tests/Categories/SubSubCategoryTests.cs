using FluentAssertions;
using TakOne.Domain.Categories.Entities;
using Xunit;

namespace TakOne.Domain.Tests.Categories;

/// <summary>
/// Unit tests for <see cref="SubSubCategory"/>.
///
/// <see cref="SubSubCategory"/>'s constructor and behavior methods are
/// <c>internal</c>, so we test them through the parent
/// <see cref="Category"/> aggregate's public API
/// (Category.AddSubSubCategory, RenameSubSubCategory,
/// DeactivateSubSubCategory, ActivateSubSubCategory).
/// </summary>
public class SubSubCategoryTests
{
    [Fact]
    public void SubSubCategory_AddedViaCategory_HasNonEmptyIdAndSubCategoryIdMatchingParent()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");

        // Act
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Assert
        subsub.Id.Should().NotBeEmpty();
        subsub.SubCategoryId.Should().Be(sub.Id);
    }

    [Fact]
    public void SubSubCategory_AddedViaCategory_StartsActiveWithCorrectName()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");

        // Act
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Assert
        subsub.Name.Should().Be("Sci-Fi");
        subsub.IsActive.Should().BeTrue();
    }

    [Fact]
    public void SubSubCategory_RenamedViaCategory_HasUpdatedName()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        var subsub = category.AddSubSubCategory(sub.Id, "Old");

        // Act
        category.RenameSubSubCategory(sub.Id, subsub.Id, "New");

        // Assert
        subsub.Name.Should().Be("New");
    }

    [Fact]
    public void SubSubCategory_DeactivatedAndReactivatedViaCategory_TogglesIsActive()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Act — deactivate then reactivate
        category.DeactivateSubSubCategory(sub.Id, subsub.Id);
        subsub.IsActive.Should().BeFalse();

        category.ActivateSubSubCategory(sub.Id, subsub.Id);

        // Assert
        subsub.IsActive.Should().BeTrue();
    }
}

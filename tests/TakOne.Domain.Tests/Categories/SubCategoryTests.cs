using FluentAssertions;
using TakOne.Domain.Categories.Entities;
using Xunit;

namespace TakOne.Domain.Tests.Categories;

/// <summary>
/// Unit tests for <see cref="SubCategory"/>.
///
/// <see cref="SubCategory"/>'s constructor and behavior methods are
/// <c>internal</c>, so we test them through the parent
/// <see cref="Category"/> aggregate's public API
/// (Category.AddSubCategory, RenameSubCategory, DeactivateSubCategory,
/// ActivateSubCategory). This file focuses on the public observable
/// properties (Id, Name, CategoryId, IsActive, SubSubCategories).
/// </summary>
public class SubCategoryTests
{
    [Fact]
    public void SubCategory_AddedViaCategory_HasNonEmptyIdAndCategoryIdMatchingParent()
    {
        // Arrange
        var category = Category.Create("Books");

        // Act
        var sub = category.AddSubCategory("Fiction");

        // Assert
        sub.Id.Should().NotBeEmpty();
        sub.CategoryId.Should().Be(category.Id);
    }

    [Fact]
    public void SubCategory_AddedViaCategory_StartsActiveWithEmptySubSubCategories()
    {
        // Arrange
        var category = Category.Create("Books");

        // Act
        var sub = category.AddSubCategory("Fiction");

        // Assert
        sub.IsActive.Should().BeTrue();
        sub.SubSubCategories.Should().BeEmpty();
    }

    [Fact]
    public void SubCategory_RenamedViaCategory_HasUpdatedName()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Old");

        // Act
        category.RenameSubCategory(sub.Id, "New");

        // Assert
        sub.Name.Should().Be("New");
    }

    [Fact]
    public void SubCategory_DeactivatedViaCategory_StopsBeingActive()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");

        // Act
        category.DeactivateSubCategory(sub.Id);

        // Assert
        sub.IsActive.Should().BeFalse();
    }

    [Fact]
    public void SubCategory_OwnsItsSubSubCategoriesCollection()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");

        // Act — add a child subsub through the Category aggregate
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Assert — the sub exposes its subsubs as a read-only collection
        sub.SubSubCategories.Should().Contain(subsub);
    }
}

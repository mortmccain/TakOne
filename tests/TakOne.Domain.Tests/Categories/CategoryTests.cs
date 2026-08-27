using FluentAssertions;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Categories.Events;
using TakOne.SharedKernel.Common;
using Xunit;

namespace TakOne.Domain.Tests.Categories;

/// <summary>
/// Unit tests for the <see cref="Category"/> aggregate root, including its
/// cascade behavior to <see cref="SubCategory"/> and <see cref="SubSubCategory"/>
/// (which are created/modified only through the Category aggregate because
/// their constructors and behavior methods are internal).
/// </summary>
public class CategoryTests
{
    // ======================================================================
    //                          CREATE — HAPPY PATH & GUARDS
    // ======================================================================

    [Fact]
    public void Create_WithValidName_ReturnsActiveCategoryWithEmptySubCategories()
    {
        // Arrange
        const string name = "Books";

        // Act
        var category = Category.Create(name);

        // Assert
        category.Id.Should().NotBeEmpty();
        category.Name.Should().Be(name);
        category.IsActive.Should().BeTrue();
        category.SubCategories.Should().BeEmpty();
    }

    [Fact]
    public void Create_RaisesCategoryCreatedDomainEvent()
    {
        // Act
        var category = Category.Create("Books");

        // Assert
        category.DomainEvents.Should().ContainSingle(e => e is CategoryCreatedDomainEvent);
        var ev = category.DomainEvents.OfType<CategoryCreatedDomainEvent>().Single();
        ev.CategoryId.Should().Be(category.Id);
        ev.Name.Should().Be("Books");
    }

    [Fact]
    public void Create_WithEmptyName_Throws()
    {
        // Arrange
        Action act = () => Category.Create("");

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Category name is required.");
    }

    [Fact]
    public void Create_WithWhitespaceName_Throws()
    {
        // Arrange — whitespace is collapsed by IsNullOrWhiteSpace
        Action act = () => Category.Create("   ");

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Category name is required.");
    }

    [Fact]
    public void Create_WithNameExceeding100Chars_Throws()
    {
        // Arrange — boundary violation
        var longName = new string('a', 101);

        Action act = () => Category.Create(longName);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Category name cannot exceed 100 characters.");
    }

    // ======================================================================
    //                          RENAME
    // ======================================================================

    [Fact]
    public void Rename_WithValidName_ChangesTheName()
    {
        // Arrange
        var category = Category.Create("Old");

        // Act
        category.Rename("New");

        // Assert
        category.Name.Should().Be("New");
    }

    [Fact]
    public void Rename_WithEmptyName_Throws()
    {
        // Arrange
        var category = Category.Create("Old");

        // Act
        Action act = () => category.Rename("");

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Category name is required.");
    }

    [Fact]
    public void Rename_WithWhitespace_Throws()
    {
        // Arrange
        var category = Category.Create("Old");

        // Act
        Action act = () => category.Rename("   ");

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Category name is required.");
    }

    [Fact]
    public void Rename_WithNameExceeding100_Throws()
    {
        // Arrange
        var category = Category.Create("Old");
        var longName = new string('a', 101);

        // Act
        Action act = () => category.Rename(longName);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Category name cannot exceed 100 characters.");
    }

    // ======================================================================
    //                          ACTIVATE / DEACTIVATE
    // ======================================================================

    [Fact]
    public void Deactivate_SetsIsActiveFalse_AndCascadesToSubCategoriesAndSubSubCategories()
    {
        // Arrange — category with one sub, which has one subsub
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Act
        category.Deactivate();

        // Assert — cascade reaches both children
        category.IsActive.Should().BeFalse();
        sub.IsActive.Should().BeFalse();
        subsub.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Activate_SetsIsActiveTrueButDoesNotReactivateSubCategories()
    {
        // Arrange — a deactivated category with deactivated subs
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        category.Deactivate(); // cascades to sub

        // Act — reactivating the category does NOT auto-reactivate its subs
        category.Activate();

        // Assert
        category.IsActive.Should().BeTrue();
        sub.IsActive.Should().BeFalse();
    }

    // ======================================================================
    //                          ADD SUBCATEGORY
    // ======================================================================

    [Fact]
    public void AddSubCategory_WithValidName_AddsSubAndSetsCategoryIdAndIsActive()
    {
        // Arrange
        var category = Category.Create("Books");

        // Act
        var sub = category.AddSubCategory("Fiction");

        // Assert
        category.SubCategories.Should().ContainSingle();
        category.SubCategories[0].Should().Be(sub);
        sub.Id.Should().NotBeEmpty();
        sub.Name.Should().Be("Fiction");
        sub.CategoryId.Should().Be(category.Id);
        sub.IsActive.Should().BeTrue();
    }

    [Fact]
    public void AddSubCategory_WhenCategoryDeactivated_Throws()
    {
        // Arrange — deactivated category cannot receive new children
        var category = Category.Create("Books");
        category.Deactivate();

        // Act
        Action act = () => category.AddSubCategory("Fiction");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage($"Cannot modify Category 'Books' because it is deactivated.");
    }

    [Fact]
    public void AddSubCategory_WithEmptyName_Throws()
    {
        // Arrange
        var category = Category.Create("Books");

        // Act
        Action act = () => category.AddSubCategory("");

        // Assert
        act.Should().Throw<DomainException>().WithMessage("SubCategory name is required.");
    }

    [Fact]
    public void AddSubCategory_WithNameExceeding100_Throws()
    {
        // Arrange
        var category = Category.Create("Books");
        var longName = new string('a', 101);

        // Act
        Action act = () => category.AddSubCategory(longName);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("SubCategory name cannot exceed 100 characters.");
    }

    [Fact]
    public void AddSubCategory_WithDuplicateNameCaseInsensitive_Throws()
    {
        // Arrange — sub "Fiction" already exists; "FICTION" must clash
        var category = Category.Create("Books");
        category.AddSubCategory("Fiction");

        // Act
        Action act = () => category.AddSubCategory("FICTION");

        // Assert — name uniqueness within a Category is case-insensitive
        act.Should().Throw<DomainException>()
            .WithMessage("A SubCategory named 'FICTION' already exists under Category 'Books'.");
    }

    // ======================================================================
    //                          RENAME SUBCATEGORY
    // ======================================================================

    [Fact]
    public void RenameSubCategory_WithValidName_ChangesTheSubName()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Old Name");

        // Act
        category.RenameSubCategory(sub.Id, "New Name");

        // Assert
        sub.Name.Should().Be("New Name");
    }

    [Fact]
    public void RenameSubCategory_WithNonExistingSubCategoryId_Throws()
    {
        // Arrange
        var category = Category.Create("Books");
        var unknownId = Guid.NewGuid();

        // Act
        Action act = () => category.RenameSubCategory(unknownId, "New");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage($"SubCategory with Id '{unknownId}' was not found under Category 'Books'.");
    }

    [Fact]
    public void RenameSubCategory_WithDuplicateNameExcludingSelf_Throws()
    {
        // Arrange — two subs: "Fiction" and "History". Renaming "History" to
        // "Fiction" must clash (excluding the rename target itself).
        var category = Category.Create("Books");
        var fiction = category.AddSubCategory("Fiction");
        var history = category.AddSubCategory("History");

        // Act — rename history to fiction
        Action act = () => category.RenameSubCategory(history.Id, "Fiction");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("A SubCategory named 'Fiction' already exists under Category 'Books'.");
    }

    [Fact]
    public void RenameSubCategory_WhenCategoryDeactivated_Throws()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        category.Deactivate();

        // Act
        Action act = () => category.RenameSubCategory(sub.Id, "New Name");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot modify Category 'Books' because it is deactivated.");
    }

    // ======================================================================
    //                          DEACTIVATE / ACTIVATE SUBCATEGORY
    // ======================================================================

    [Fact]
    public void DeactivateSubCategory_SetsSubInactiveAndCascadesToSubSubCategories()
    {
        // Arrange — sub with one subsub
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Act
        category.DeactivateSubCategory(sub.Id);

        // Assert — cascade reaches the subsub too
        sub.IsActive.Should().BeFalse();
        subsub.IsActive.Should().BeFalse();
        category.IsActive.Should().BeTrue(); // parent unaffected
    }

    [Fact]
    public void DeactivateSubCategory_WithNonExistingId_Throws()
    {
        // Arrange
        var category = Category.Create("Books");
        var unknownId = Guid.NewGuid();

        // Act
        Action act = () => category.DeactivateSubCategory(unknownId);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage($"SubCategory with Id '{unknownId}' was not found under Category 'Books'.");
    }

    [Fact]
    public void ActivateSubCategory_SetsSubActive()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        category.DeactivateSubCategory(sub.Id);

        // Act
        category.ActivateSubCategory(sub.Id);

        // Assert
        sub.IsActive.Should().BeTrue();
    }

    // ======================================================================
    //                          ADD SUBSUBCATEGORY
    // ======================================================================

    [Fact]
    public void AddSubSubCategory_WithValidName_AddsSubSubUnderTheGivenSub()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");

        // Act
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Assert
        sub.SubSubCategories.Should().ContainSingle();
        sub.SubSubCategories[0].Should().Be(subsub);
        subsub.Id.Should().NotBeEmpty();
        subsub.Name.Should().Be("Sci-Fi");
        subsub.SubCategoryId.Should().Be(sub.Id);
        subsub.IsActive.Should().BeTrue();
    }

    [Fact]
    public void AddSubSubCategory_WhenCategoryDeactivated_Throws()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        category.Deactivate();

        // Act
        Action act = () => category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot modify Category 'Books' because it is deactivated.");
    }

    [Fact]
    public void AddSubSubCategory_WhenSubCategoryDeactivated_Throws()
    {
        // Arrange — sub is deactivated; cannot add children to it
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        category.DeactivateSubCategory(sub.Id);

        // Act
        Action act = () => category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot modify SubCategory 'Fiction' because it is deactivated.");
    }

    [Fact]
    public void AddSubSubCategory_WithNonExistingSubCategoryId_Throws()
    {
        // Arrange
        var category = Category.Create("Books");
        var unknownId = Guid.NewGuid();

        // Act
        Action act = () => category.AddSubSubCategory(unknownId, "Sci-Fi");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage($"SubCategory with Id '{unknownId}' was not found under Category 'Books'.");
    }

    [Fact]
    public void AddSubSubCategory_WithDuplicateName_Throws()
    {
        // Arrange — subsub "Sci-Fi" already exists; adding another must clash
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Act
        Action act = () => category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Assert — name uniqueness within a SubCategory is case-insensitive
        act.Should().Throw<DomainException>()
            .WithMessage("A SubSubCategory named 'Sci-Fi' already exists under SubCategory 'Fiction'.");
    }

    // ======================================================================
    //                          RENAME SUBSUBCATEGORY
    // ======================================================================

    [Fact]
    public void RenameSubSubCategory_WithValidName_ChangesTheSubSubName()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Act
        category.RenameSubSubCategory(sub.Id, subsub.Id, "Cyberpunk");

        // Assert
        subsub.Name.Should().Be("Cyberpunk");
    }

    [Fact]
    public void RenameSubSubCategory_WithNonExistingIds_Throws()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        var unknownSubSubId = Guid.NewGuid();

        // Act — the SubCategory exists, but the SubSubCategoryId does not
        Action act = () => category.RenameSubSubCategory(sub.Id, unknownSubSubId, "X");

        // Assert — error message comes from SubCategory.EnsureSubSubCategoryExists
        act.Should().Throw<DomainException>()
            .WithMessage($"SubSubCategory with Id '{unknownSubSubId}' was not found under SubCategory 'Fiction'.");
    }

    // ======================================================================
    //                          DEACTIVATE / ACTIVATE SUBSUBCATEGORY
    // ======================================================================

    [Fact]
    public void DeactivateSubSubCategory_SetsSubSubInactive()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");

        // Act
        category.DeactivateSubSubCategory(sub.Id, subsub.Id);

        // Assert
        subsub.IsActive.Should().BeFalse();
    }

    [Fact]
    public void ActivateSubSubCategory_SetsSubSubActive()
    {
        // Arrange
        var category = Category.Create("Books");
        var sub = category.AddSubCategory("Fiction");
        var subsub = category.AddSubSubCategory(sub.Id, "Sci-Fi");
        category.DeactivateSubSubCategory(sub.Id, subsub.Id);

        // Act
        category.ActivateSubSubCategory(sub.Id, subsub.Id);

        // Assert
        subsub.IsActive.Should().BeTrue();
    }
}

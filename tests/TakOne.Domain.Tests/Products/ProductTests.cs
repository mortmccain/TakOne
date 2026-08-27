using FluentAssertions;
using TakOne.Domain.Products.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.Domain.Tests.Products;

/// <summary>
/// Unit tests for the <see cref="Product"/> aggregate root.
/// Verifies factory validation, stock-mutation methods (Increase/Decrease/Set/Adjust),
/// UpdateDetails, UpdateCategory, and per-group purchase-limit lifecycle.
/// </summary>
public class ProductTests
{
    // ======================================================================
    //                          CREATE — HAPPY PATH
    // ======================================================================

    [Fact]
    public void Create_WithValidArgs_ReturnsProductWithCorrectProperties()
    {
        // Arrange
        var name = "Test Product";
        var description = "A test product";
        var price = new Money(100m, TestValues.USD);

        // Act
        var product = Product.Create(
            name,
            description,
            price,
            stockQuantity: 10,
            categoryId: TestValues.CategoryId);

        // Assert
        product.Id.Should().NotBeEmpty();
        product.Name.Should().Be(name);
        product.Description.Should().Be(description);
        product.Price.Should().Be(price);
        product.StockQuantity.Should().Be(10);
        product.CategoryId.Should().Be(TestValues.CategoryId);
        product.PictureUrl.Should().BeNull();
        product.SubCategoryId.Should().BeNull();
        product.SubSubCategoryId.Should().BeNull();
    }

    [Fact]
    public void Create_WithPictureUrlAndSubCategoryIds_AssignsAllOptionalFields()
    {
        // Arrange
        const string pictureUrl = "https://example.com/img.png";

        // Act
        var product = Product.Create(
            name: "P",
            description: "D",
            price: new Money(1m, TestValues.USD),
            stockQuantity: 5,
            categoryId: TestValues.CategoryId,
            pictureUrl: pictureUrl,
            subCategoryId: TestValues.SubCategoryId,
            subSubCategoryId: TestValues.SubSubCategoryId);

        // Assert
        product.PictureUrl.Should().Be(pictureUrl);
        product.SubCategoryId.Should().Be(TestValues.SubCategoryId);
        product.SubSubCategoryId.Should().Be(TestValues.SubSubCategoryId);
    }

    [Fact]
    public void Create_WithNullPictureUrl_AllowsNull()
    {
        // Arrange — pictureUrl is optional; null is the default
        // Act
        var product = new ProductBuilder().WithPictureUrl(null).Build();

        // Assert
        product.PictureUrl.Should().BeNull();
    }

    [Fact]
    public void Create_WithNullSubAndSubSubCategoryId_AllowsTopLevelProduct()
    {
        // Arrange — both optional Guid?s null → top-level product (only assigned a Category)
        // Act
        var product = Product.Create(
            name: "P",
            description: "D",
            price: new Money(1m, TestValues.USD),
            stockQuantity: 5,
            categoryId: TestValues.CategoryId);

        // Assert
        product.SubCategoryId.Should().BeNull();
        product.SubSubCategoryId.Should().BeNull();
    }

    // ======================================================================
    //                          CREATE — GUARDS
    // ======================================================================

    [Fact]
    public void Create_WithSubSubCategoryIdButNullSubCategoryId_Throws()
    {
        // Arrange — subsubcategory-without-subcategory is a broken hierarchy
        Action act = () => Product.Create(
            name: "P",
            description: "D",
            price: new Money(1m, TestValues.USD),
            stockQuantity: 5,
            categoryId: TestValues.CategoryId,
            pictureUrl: null,
            subCategoryId: null,
            subSubCategoryId: TestValues.SubSubCategoryId);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot assign a SubSubCategory without a SubCategory.");
    }

    [Fact]
    public void Create_WithEmptyName_Throws()
    {
        // Arrange
        Action act = () => new ProductBuilder().WithName("").Build();

        // Assert
        act.Should().Throw<DomainException>().WithMessage("name is required.");
    }

    [Fact]
    public void Create_WithWhitespaceName_Throws()
    {
        // Arrange — IsNullOrWhiteSpace collapses whitespace to "empty"
        Action act = () => new ProductBuilder().WithName("   ").Build();

        // Assert
        act.Should().Throw<DomainException>().WithMessage("name is required.");
    }

    [Fact]
    public void Create_WithNameExceeding200Chars_Throws()
    {
        // Arrange — boundary violation: name length 201
        var longName = new string('a', 201);

        Action act = () => new ProductBuilder().WithName(longName).Build();

        // Assert
        act.Should().Throw<DomainException>().WithMessage("name cannot exceed 200 characters.");
    }

    [Fact]
    public void Create_WithEmptyDescription_Throws()
    {
        // Arrange
        Action act = () => new ProductBuilder().WithDescription("").Build();

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Product description is required.");
    }

    [Fact]
    public void Create_WithDescriptionExceeding2000Chars_Throws()
    {
        // Arrange — boundary violation: description length 2001
        var longDescription = new string('d', 2001);

        Action act = () => new ProductBuilder().WithDescription(longDescription).Build();

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Product description cannot exceed 2000 characters.");
    }

    [Fact]
    public void Create_WithNegativePrice_Throws()
    {
        // Arrange
        var negativePrice = new Money(-1m, TestValues.USD);

        Action act = () => new ProductBuilder().WithPrice(negativePrice).Build();

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Product price cannot be negative.");
    }

    [Fact]
    public void Create_WithEmptyCategoryId_Throws()
    {
        // Arrange
        Action act = () => new ProductBuilder().WithCategory(Guid.Empty).Build();

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Category ID is required.");
    }

    [Fact]
    public void Create_WithNegativeStock_Throws()
    {
        // Arrange
        Action act = () => new ProductBuilder().WithStock(-1).Build();

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Stock quantity cannot be negative.");
    }

    // ======================================================================
    //                          STOCK MANAGEMENT
    // ======================================================================

    [Fact]
    public void IncreaseStock_WithPositiveQuantity_AddsToStockQuantity()
    {
        // Arrange
        var product = new ProductBuilder().WithStock(10).Build();

        // Act
        product.IncreaseStock(5);

        // Assert
        product.StockQuantity.Should().Be(15);
    }

    [Fact]
    public void IncreaseStock_WithZero_Throws()
    {
        // Arrange
        var product = new ProductBuilder().WithStock(10).Build();

        // Act
        Action act = () => product.IncreaseStock(0);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Quantity to increase must be greater than zero.");
    }

    [Fact]
    public void IncreaseStock_WithNegative_Throws()
    {
        // Arrange
        var product = new ProductBuilder().WithStock(10).Build();

        // Act
        Action act = () => product.IncreaseStock(-3);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Quantity to increase must be greater than zero.");
    }

    [Fact]
    public void DecreaseStock_WithQuantityLessThanOrEqualStock_SubtractsFromStock()
    {
        // Arrange
        var product = new ProductBuilder().WithStock(10).Build();

        // Act
        product.DecreaseStock(3);

        // Assert
        product.StockQuantity.Should().Be(7);
    }

    [Fact]
    public void DecreaseStock_WithQuantityGreaterThanStock_Throws()
    {
        // Arrange
        var product = new ProductBuilder().WithStock(2).Build();

        // Act
        Action act = () => product.DecreaseStock(5);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Insufficient stock to remove the specified quantity.");
    }

    [Fact]
    public void DecreaseStock_WithZero_Throws()
    {
        // Arrange
        var product = new ProductBuilder().WithStock(10).Build();

        // Act
        Action act = () => product.DecreaseStock(0);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Quantity to decrease must be greater than zero.");
    }

    [Fact]
    public void DecreaseStock_WithNegative_Throws()
    {
        // Arrange
        var product = new ProductBuilder().WithStock(10).Build();

        // Act
        Action act = () => product.DecreaseStock(-1);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Quantity to decrease must be greater than zero.");
    }

    [Fact]
    public void SetStock_WithValidQuantity_SetsTheValue()
    {
        // Arrange
        var product = new ProductBuilder().WithStock(10).Build();

        // Act
        product.SetStock(42);

        // Assert
        product.StockQuantity.Should().Be(42);
    }

    [Fact]
    public void SetStock_WithNegative_Throws()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.SetStock(-1);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Stock quantity cannot be negative.");
    }

    [Fact]
    public void SetStock_AcceptsZero_ForDeactivationFlow()
    {
        // Arrange — SetStock(0) is used by the deactivation handler to zero out stock
        var product = new ProductBuilder().WithStock(10).Build();

        // Act
        product.SetStock(0);

        // Assert
        product.StockQuantity.Should().Be(0);
    }

    [Fact]
    public void AdjustStockTo_WithPositiveQuantity_SetsTheValue()
    {
        // Arrange
        var product = new ProductBuilder().WithStock(10).Build();

        // Act
        product.AdjustStockTo(33);

        // Assert
        product.StockQuantity.Should().Be(33);
    }

    [Fact]
    public void AdjustStockTo_WithZero_Throws()
    {
        // Arrange — UI must NOT allow 0; user must deactivate instead
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.AdjustStockTo(0);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Stock quantity must be greater than zero. To make it zero, deactivate the product instead.");
    }

    [Fact]
    public void AdjustStockTo_WithNegative_Throws()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.AdjustStockTo(-5);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Stock quantity must be greater than zero. To make it zero, deactivate the product instead.");
    }

    // ======================================================================
    //                          DETAIL / CATEGORY UPDATES
    // ======================================================================

    [Fact]
    public void UpdateDetails_WithValidArgs_UpdatesAllFields()
    {
        // Arrange
        var product = new ProductBuilder().Build();
        var newPrice = new Money(250m, TestValues.USD);
        const string newPictureUrl = "https://example.com/new.png";

        // Act
        product.UpdateDetails("New Name", "New Description", newPrice, newPictureUrl);

        // Assert
        product.Name.Should().Be("New Name");
        product.Description.Should().Be("New Description");
        product.Price.Should().Be(newPrice);
        product.PictureUrl.Should().Be(newPictureUrl);
    }

    [Fact]
    public void UpdateDetails_WithEmptyName_Throws()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.UpdateDetails(
            name: "",
            description: "D",
            price: new Money(1m, TestValues.USD),
            pictureUrl: null);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("name is required.");
    }

    [Fact]
    public void UpdateDetails_WithEmptyDescription_Throws()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.UpdateDetails(
            name: "N",
            description: "",
            price: new Money(1m, TestValues.USD),
            pictureUrl: null);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Product description is required.");
    }

    [Fact]
    public void UpdateDetails_WithNegativePrice_Throws()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.UpdateDetails(
            name: "N",
            description: "D",
            price: new Money(-1m, TestValues.USD),
            pictureUrl: null);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Product price cannot be negative.");
    }

    [Fact]
    public void UpdateCategory_WithCategoryIdOnly_ClearsSubAndSubSubCategory()
    {
        // Arrange — start with a product that has sub + subsub refs; use a
        // different category Guid so we can assert it actually changed.
        var newCategoryId = Guid.Parse("0a0a0a0a-0a0a-0a0a-0a0a-0a0a0a0a0a0a");
        var product = new ProductBuilder()
            .WithSubCategory(TestValues.SubCategoryId)
            .WithSubSubCategory(TestValues.SubSubCategoryId)
            .Build();

        // Act — switch to top-level category only, no sub/subsub
        product.UpdateCategory(newCategoryId);

        // Assert
        product.CategoryId.Should().Be(newCategoryId);
        product.SubCategoryId.Should().BeNull();
        product.SubSubCategoryId.Should().BeNull();
    }

    [Fact]
    public void UpdateCategory_WithSubSubCategoryIdButNullSubCategoryId_Throws()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.UpdateCategory(
            categoryId: TestValues.CategoryId,
            subCategoryId: null,
            subSubCategoryId: TestValues.SubSubCategoryId);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot assign a SubSubCategory without a SubCategory.");
    }

    [Fact]
    public void UpdateCategory_WithEmptyCategoryId_Throws()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.UpdateCategory(Guid.Empty);

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Category ID is required.");
    }

    // ======================================================================
    //                          PURCHASE LIMIT MANAGEMENT
    // ======================================================================

    [Fact]
    public void SetPurchaseLimit_WithNewGroupId_AddsLimitToCollection()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        product.SetPurchaseLimit(TestValues.GroupId, 5);

        // Assert
        product.PurchaseLimits.Should().HaveCount(1);
        var limit = product.PurchaseLimits[0];
        limit.GroupId.Should().Be(TestValues.GroupId);
        limit.Limit.Should().Be(5);
    }

    [Fact]
    public void SetPurchaseLimit_WithExistingGroupId_ReplacesLimit()
    {
        // Arrange — start with limit=5 for group A
        var product = new ProductBuilder().Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);

        // Act — replace with limit=10 for same group
        product.SetPurchaseLimit(TestValues.GroupId, 10);

        // Assert
        product.PurchaseLimits.Should().HaveCount(1); // replaced, not added
        product.PurchaseLimits[0].GroupId.Should().Be(TestValues.GroupId);
        product.PurchaseLimits[0].Limit.Should().Be(10);
    }

    [Fact]
    public void SetPurchaseLimit_WithEmptyGroupId_Throws()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.SetPurchaseLimit(Guid.Empty, 1);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Group Id is required to set a purchase limit.");
    }

    [Fact]
    public void RemovePurchaseLimit_WithExistingGroupId_RemovesIt()
    {
        // Arrange
        var product = new ProductBuilder().Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);

        // Act
        product.RemovePurchaseLimit(TestValues.GroupId);

        // Assert
        product.PurchaseLimits.Should().BeEmpty();
    }

    [Fact]
    public void RemovePurchaseLimit_WithNonExistingGroupId_IsNoOp()
    {
        // Arrange — no limit set for GroupId2 yet
        var product = new ProductBuilder().Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);

        // Act — remove a different (un-set) group's limit; should be silent no-op
        product.RemovePurchaseLimit(TestValues.GroupId2);

        // Assert
        product.PurchaseLimits.Should().HaveCount(1); // unchanged
    }

    [Fact]
    public void RemovePurchaseLimit_WithEmptyGroupId_Throws()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        Action act = () => product.RemovePurchaseLimit(Guid.Empty);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Group Id is required to remove a purchase limit.");
    }

    [Fact]
    public void GetPurchaseLimitForGroup_WhenLimitSet_ReturnsTheLimit()
    {
        // Arrange
        var product = new ProductBuilder().Build();
        product.SetPurchaseLimit(TestValues.GroupId, 7);

        // Act
        var limit = product.GetPurchaseLimitForGroup(TestValues.GroupId);

        // Assert
        limit.Should().NotBeNull();
        limit!.GroupId.Should().Be(TestValues.GroupId);
        limit.Limit.Should().Be(7);
    }

    [Fact]
    public void GetPurchaseLimitForGroup_WhenNoLimitSet_ReturnsNull()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act
        var limit = product.GetPurchaseLimitForGroup(TestValues.GroupId);

        // Assert
        limit.Should().BeNull();
    }

    [Fact]
    public void GetPurchaseLimitForGroup_WithEmptyGroupId_ReturnsNull()
    {
        // Arrange
        var product = new ProductBuilder().Build();

        // Act — defensive: empty Guid short-circuits to null
        var limit = product.GetPurchaseLimitForGroup(Guid.Empty);

        // Assert
        limit.Should().BeNull();
    }
}

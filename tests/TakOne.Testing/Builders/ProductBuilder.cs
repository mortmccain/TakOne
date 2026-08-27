namespace TakOne.Testing.Builders;

using TakOne.Domain.Products.Entities;
using TakOne.Domain.Products.ValueObjects;
using TakOne.SharedKernel.ValueObjects;

/// <summary>
/// Test-data builder for the <see cref="Product"/> aggregate. Yields a
/// valid Product with sensible defaults; call the With* methods to
/// override individual fields per-test. Every With* returns a new
/// builder instance so the fluent chain is immutable (no hidden
/// shared state between tests).
/// </summary>
public sealed class ProductBuilder
{
    private string _name = "Test Product";
    private string _description = "Test description";
    private Money _price = new(100m, TestValues.USD);
    private int _stockQuantity = 10;
    private Guid _categoryId = TestValues.CategoryId;
    private string? _pictureUrl = null;
    private Guid? _subCategoryId = null;
    private Guid? _subSubCategoryId = null;

    public ProductBuilder WithName(string name) { return new() { _name = name, _description = _description, _price = _price, _stockQuantity = _stockQuantity, _categoryId = _categoryId, _pictureUrl = _pictureUrl, _subCategoryId = _subCategoryId, _subSubCategoryId = _subSubCategoryId }; }
    public ProductBuilder WithDescription(string description) { return new() { _name = _name, _description = description, _price = _price, _stockQuantity = _stockQuantity, _categoryId = _categoryId, _pictureUrl = _pictureUrl, _subCategoryId = _subCategoryId, _subSubCategoryId = _subSubCategoryId }; }
    public ProductBuilder WithPrice(Money price) { return new() { _name = _name, _description = _description, _price = price, _stockQuantity = _stockQuantity, _categoryId = _categoryId, _pictureUrl = _pictureUrl, _subCategoryId = _subCategoryId, _subSubCategoryId = _subSubCategoryId }; }
    public ProductBuilder WithStock(int stock) { return new() { _name = _name, _description = _description, _price = _price, _stockQuantity = stock, _categoryId = _categoryId, _pictureUrl = _pictureUrl, _subCategoryId = _subCategoryId, _subSubCategoryId = _subSubCategoryId }; }
    public ProductBuilder WithCategory(Guid categoryId) { return new() { _name = _name, _description = _description, _price = _price, _stockQuantity = _stockQuantity, _categoryId = categoryId, _pictureUrl = _pictureUrl, _subCategoryId = _subCategoryId, _subSubCategoryId = _subSubCategoryId }; }
    public ProductBuilder WithPictureUrl(string? url) { return new() { _name = _name, _description = _description, _price = _price, _stockQuantity = _stockQuantity, _categoryId = _categoryId, _pictureUrl = url, _subCategoryId = _subCategoryId, _subSubCategoryId = _subSubCategoryId }; }
    public ProductBuilder WithSubCategory(Guid? subId) { return new() { _name = _name, _description = _description, _price = _price, _stockQuantity = _stockQuantity, _categoryId = _categoryId, _pictureUrl = _pictureUrl, _subCategoryId = subId, _subSubCategoryId = _subSubCategoryId }; }
    public ProductBuilder WithSubSubCategory(Guid? subSubId) { return new() { _name = _name, _description = _description, _price = _price, _stockQuantity = _stockQuantity, _categoryId = _categoryId, _pictureUrl = _pictureUrl, _subCategoryId = _subCategoryId, _subSubCategoryId = subSubId }; }

    public Product Build() => Product.Create(
        name: _name,
        description: _description,
        price: _price,
        stockQuantity: _stockQuantity,
        categoryId: _categoryId,
        pictureUrl: _pictureUrl,
        subCategoryId: _subCategoryId,
        subSubCategoryId: _subSubCategoryId);
}

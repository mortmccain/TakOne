using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Products.Entities;

public sealed class Product : AggregateRoot
{



    // ==================================================================================================================================
    //                                                          PRIVATE FIELDS
    // ==================================================================================================================================



    private readonly List<BuyerGroupPurchaseLimit> _BuyerGroupPurchaseLimit;



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    public string Name { get; private set; }
    public string Description { get; private set; }
    public Money Price { get; private set; }
    public int StockQuantity { get; private set; }
    public IReadOnlyList<BuyerGroupPurchaseLimit> BuyerGroupPurchaseLimits => _BuyerGroupPurchaseLimit.AsReadOnly();


    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



    public Product(Guid id, string name, string description, Money price, int stockQuantity)
    {
        Id = id;
        Name = name;
        Description = description;
        Price = price;
        StockQuantity = stockQuantity;
    }



    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    private void EnsurePriceValidity(Money price)
    {
        if (price.Amount < 0)
        {
            throw new ArgumentException("Price cannot be negative.", nameof(price));
        }
    }

   private void EnsureStockQuantityValidity(int stockQuantity)
    {
        if (stockQuantity < 0)
        {
            throw new ArgumentException("Stock quantity cannot be negative.", nameof(stockQuantity));
        }
    }

    private void EnsureNameValidity(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Product name cannot be null or whitespace.", nameof(name));
        }
    }

    private void EnsureDescriptionValidity(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Product description cannot be null or whitespace.", nameof(description));
        }
    }

    private void EnsureAvailableInStock(int quantity)
    {
        if (quantity > StockQuantity)
        {
            throw new InvalidOperationException("Insufficient stock available.");
        }
    }

    private void EnsureDoesNotExceedPurchaseLimit(int quantity, string buyerGroup)
    {
        var limit = _BuyerGroupPurchaseLimit.FirstOrDefault(l => l.BuyerGroupId == buyerGroup.Id);
        if (limit != null && quantity > limit.PurchaseLimit)
        {
            throw new InvalidOperationException($"Purchase quantity exceeds the limit for buyer group {buyerGroup.Name}.");
        }
    }

    private void EnsureProductDoesNotExist(string name)
    {
        if (_products.Any(p => p.Name == name))
        {
            throw new InvalidOperationException("A product with the specified name already exists.");
        }
    }











    public void IncreaseStock(int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("Quantity to increase must be greater than zero.", nameof(quantity));
        }
        StockQuantity += quantity;
    }

    public void DecreaseStock(int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("Quantity to decrease must be greater than zero.", nameof(quantity));
        }
        if (quantity > StockQuantity)
        {
            throw new InvalidOperationException("Insufficient stock to remove the specified quantity.");
        }
        StockQuantity -= quantity;
    }

}
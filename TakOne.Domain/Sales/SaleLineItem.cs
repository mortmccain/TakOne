using System;
using System.Collections.Generic;
using System.Text;

namespace TakOne.Domain.Sales;

using ERP.SharedKernel.Common;
using ERP.SharedKernel.Primitives;
using ERP.SharedKernel.ValueObjects;

public sealed class SaleLineItem : BaseEntity
{

    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; }
    // don't need it here YAGNI (you ain't gonna need it)
    // public string ProductDescription { get;private set;}
    public string ProductCategory { get; private set; }
    // Stock Keeping Unit (unique code for each product variant)
    public string? SKU { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public int LineNumber { get; private set; }

    // --- Line Level Discount --- 
    public decimal DiscountPercentage { get; private set; }
    public string? DiscountReason { get; private set; }
    public Money DiscountAmount => GrossTotal * (DiscountPercentage / 100m);

    // --- FOC (Free of Charge) ---
    public bool IsFreeOfCharge { get; private set; }
    public string? FocReason { get; private set; }

    // --- Computed Totals ---
    public Money GrossTotal => Quantity * UnitPrice;
    public Money LineTotal
    {
        get
        {
            if (IsFreeOfCharge) return Money.Zero(UnitPrice.Currency);

            if (DiscountPercentage > 0) return GrossTotal - DiscountAmount;

            return GrossTotal;
        }
    }



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================


#pragma warning disable CS8618
    private SaleLineItem() : base(Guid.Empty) { }   // for the EF core
#pragma warning restore CS8618
    // internal so it only works within the same assembly
    internal SaleLineItem
        (
        Guid productId,
        string productName,
        string? sku,
        int quantity,
        Money unitPrice,
        int lineNumber,
        string productCategory
        )
        : base(Guid.NewGuid()) // Generate a new unique ID for this line item
    {
        ProductId = productId;
        ProductName = productName;
        SKU = sku;
        Quantity = quantity;
        UnitPrice = unitPrice;
        // have this for auditing. when a saleline gets deleted,
        // the auditor might ask us for line 3 and the deleted lines make things difficult since line 3 might not exist as line 3 anymore
        LineNumber = lineNumber;
        ProductCategory = productCategory;

        DiscountPercentage = 0;
        IsFreeOfCharge = false;
    }



    // ==================================================================================================================================
    //                                                          BEHAVIOR (METHODS)
    // ==================================================================================================================================



    // these bad boys should be validated inside aggregate roots
    internal void UpdateQuantity(int newQuantity)
    {
        Quantity = newQuantity;
    }

    internal void UpdateUnitPrice(Money newPrice)
    {
        UnitPrice = newPrice;
    }



    /// <summary>
    /// Applies a percentage discount to this specific line item.
    /// </summary>
    internal void ApplyLineDiscount(decimal percentage, string reason)
    {
        if (percentage < 0 || percentage > 100)
            throw new DomainException("Line discount percentage must be between 0 and 100.");

        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Discount reason is required");

        if (IsFreeOfCharge)
            throw new DomainException("Cannot apply a discount to a free-of-charge item.");

        DiscountPercentage = percentage;
        DiscountReason = reason;
    }



    /// <summary>
    /// Removes the line-level discount from this line item.
    /// </summary>
    internal void RemoveLineDiscount()
    {
        DiscountPercentage = 0m;
        DiscountReason = null;
    }



    /// <summary>
    /// Marks this line item as free of charge.
    /// </summary>
    internal void MarkAsFreeOfCharge(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("A reason is required for FOC items.");

        IsFreeOfCharge = true;
        FocReason = reason;
        DiscountPercentage = 0m;
        DiscountReason = null;
    }



    /// <summary>
    /// Removes the FOC status from this line item, making it chargeable again.
    /// </summary>
    internal void UnmarkAsFreeOfCharge()
    {
        IsFreeOfCharge = false;
        FocReason = null;
    }

}

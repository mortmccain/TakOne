using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Sales.Entities;



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
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public int LineNumber { get; private set; }

    // --- Computed Totals ---
    public Money GrossTotal => Quantity * UnitPrice;



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
        int quantity,
        Money unitPrice,
        int lineNumber
        )
        : base(Guid.NewGuid()) // Generate a new unique ID for this line item
    {
        ProductId = productId;
        ProductName = productName;
        Quantity = quantity;
        UnitPrice = unitPrice;
        // have this for auditing. when a saleline gets deleted,
        // the auditor might ask us for line 3 and the deleted lines make things difficult since line 3 might not exist as line 3 anymore
        LineNumber = lineNumber;
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
}

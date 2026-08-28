using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Domain.Sales.Entities;

/// <summary>
/// A single line on a Sale. Lives inside the Sale aggregate boundary.
///
/// INVARIANTS (enforced by the parent Sale aggregate):
///   - Quantity must be a positive integer.
///   - UnitPrice cannot be negative.
///   - ProductId must be a non-empty Guid.
///   - ProductName must be non-empty.
///   - LineNumber is assigned by the parent Sale and is stable for audit
///     (line 3 stays line 3 even after line 2 is deleted).
/// </summary>
public sealed class SaleLineItem : BaseEntity
{



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; }

    /// <summary>
    /// Quantity ordered on this line. Always ≥ 1 (enforced by parent Sale).
    /// </summary>
    public int Quantity { get; private set; }

    /// <summary>
    /// Unit price snapshot at the time the line was added. Stored as a snapshot
    /// (not a reference to Product.Price) so that future price changes on the
    /// Product don't alter historical sales.
    /// </summary>
    public Money UnitPrice { get; private set; }

    /// <summary>
    /// Audit-friendly position of this line on the sale (1, 2, 3, ...).
    /// Stable: deleting line 2 does NOT renumber line 3.
    /// </summary>
    public int LineNumber { get; private set; }

    /// <summary>
    /// Computed gross total for this line. NOT stored — recalculated on read.
    /// </summary>
    public Money GrossTotal => Quantity * UnitPrice;



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by EF Core. DO NOT use in application code.
    /// </summary>
    private SaleLineItem() : base(Guid.Empty) { }
#pragma warning restore CS8618

    /// <summary>
    /// Internal constructor. Only Sale (the aggregate root, same assembly)
    /// can create SaleLineItems, via <see cref="Sale.AddLineItem"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>UNIT PRICE ASSIGNMENT — NO DEFENSIVE COPY:</b> Money is a sealed,
    /// immutable value object (all setters private; arithmetic operators
    /// always return new instances). Holding the caller's Money reference
    /// directly is safe — there is no API surface on Money that mutates an
    /// existing instance. The historical defensive-copy workaround
    /// (<c>new Money(unitPrice.Amount, unitPrice.Currency)</c>) was a leftover
    /// from when Money was mapped as an EF Core OWNED ENTITY: the change
    /// tracker tracked Money by reference identity, so two SaleLineItems
    /// holding the same caller Money instance could confuse the tracker
    /// into thinking both lines were "the same owned entity". With the
    /// EF Core 9+ <c>ComplexProperty</c> mapping (see SaleLineItemConfiguration
    /// for the full rationale), Money has no identity of its own in the
    /// change tracker — it's compared by value (via
    /// <see cref="BaseValueObject.GetEqualityComponents"/>), and the
    /// reference-replacement pattern (<c>li.UnitPrice = someNewMoney</c>)
    /// works correctly. The defensive copy was therefore removed in
    /// Brutal Code Review v3 finding #15 (Round 18-C).
    /// </para>
    /// </remarks>
    internal SaleLineItem(
        Guid productId,
        string productName,
        int quantity,
        Money unitPrice,
        int lineNumber) : base(Guid.NewGuid())
    {
        EnsureProductIdValid(productId);
        EnsureProductNameValid(productName);
        EnsureQuantityValid(quantity);
        EnsureUnitPriceValid(unitPrice);
        EnsureLineNumberValid(lineNumber);

        ProductId = productId;
        ProductName = productName;
        Quantity = quantity;

        // Money is sealed + immutable (private setters, no mutating methods).
        // With ComplexProperty mapping (no reference-identity tracking),
        // holding the caller's Money reference is safe — see the XML doc above.
        UnitPrice = unitPrice;

        LineNumber = lineNumber;
    }



    // ==================================================================================================================================
    //                                                          BEHAVIOR (internal)
    // ==================================================================================================================================



    /// <summary>
    /// Replaces the quantity on this line. The parent Sale is responsible for
    /// re-validating the purchase limit AFTER calling this, because the limit
    /// check needs the GroupName context which lives on the Sale.
    /// </summary>
    internal void UpdateQuantity(int newQuantity)
    {
        EnsureQuantityValid(newQuantity);
        Quantity = newQuantity;
    }



    // ==================================================================================================================================
    //                                                          GUARD METHODS
    // ==================================================================================================================================



    private static void EnsureProductIdValid(Guid productId)
    {
        if (productId == Guid.Empty)
            throw new DomainException("ProductId is required on a SaleLineItem.");
    }

    private static void EnsureProductNameValid(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
            throw new DomainException("ProductName is required on a SaleLineItem.");

        if (productName.Length > 200)
            throw new DomainException("ProductName cannot exceed 200 characters.");
    }

    private static void EnsureQuantityValid(int quantity)
    {
        if (quantity <= 0)
            throw new DomainException("Quantity must be a positive integer.");
    }

    private static void EnsureUnitPriceValid(Money unitPrice)
    {
        if (unitPrice.Amount < 0)
            throw new DomainException("Unit price cannot be negative.");
    }

    private static void EnsureLineNumberValid(int lineNumber)
    {
        if (lineNumber < 1)
            throw new DomainException("LineNumber must be at least 1.");
    }
}
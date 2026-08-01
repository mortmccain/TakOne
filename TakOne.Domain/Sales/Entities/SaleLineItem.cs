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
    /// <b>DEFENSIVE COPY OF <paramref name="unitPrice"/>:</b>
    /// <see cref="Money"/> is a <c>class</c> (reference type) mapped as an
    /// <c>OwnsOne</c> value object on BOTH <c>Product.Price</c> and
    /// <c>SaleLineItem.UnitPrice</c>. If the caller passes the SAME
    /// <c>Money</c> instance that is already owned by a tracked
    /// <c>Product</c> (which is exactly what every sale-creation handler
    /// does — they pass <c>product.Price</c> directly), EF Core's change
    /// tracker ends up with one <c>Money</c> instance claimed by two
    /// different aggregate roots. On <c>SaveChangesAsync</c> this confuses
    /// the change tracker into emitting a spurious UPDATE against the
    /// existing <c>Product</c> row whose WHERE clause matches 0 rows, which
    /// surfaces as:
    /// <c>DbUpdateConcurrencyException: The database operation was expected
    /// to affect 1 row(s), but actually affected 0 row(s)</c>.
    /// </para>
    /// <para>
    /// Value objects should never be shared across aggregate boundaries —
    /// even when immutable — because EF Core tracks owned types by
    /// reference identity, not by value equality. We therefore clone the
    /// incoming <c>Money</c> here so each <c>SaleLineItem</c> owns a
    /// distinct <c>Money</c> instance, while preserving value semantics.
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

        // DEFENSIVE COPY — see XML doc on this constructor for the full
        // rationale. Never assign the caller's Money reference directly;
        // always construct a fresh instance with the same value.
        UnitPrice = new Money(unitPrice.Amount, unitPrice.Currency);

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
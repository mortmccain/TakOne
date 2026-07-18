using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Sales.Events;

namespace TakOne.Domain.Sales.Entities;

public sealed class Sale : AggregateRoot
{


    // ==================================================================================================================================
    //                                                          PRIVATE FIELDS
    // ==================================================================================================================================



    private readonly List<SaleLineItem> _lineItems = new();



    // ==================================================================================================================================
    //                                                          PROPERTIES
    // ==================================================================================================================================



    // --- identity and reference ---
    public SaleNumber SaleNumber { get; }
    public Guid BuyerId { get; private set; }        // can't change after the creation of the sale so we have no set 
    // though it COULD change huh?
    public string BuyerName { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public Guid ApprovedByUserId { get; private set; }
    public string CreatedByName { get; private set; }



    // --- status ---
    public SaleStatus Status { get; private set; }

    // --- financial breakdown
    public Money Total { get; private set; }

    // --- timestamps ---
    public DateTime CreatedAtUtc { get; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public DateTime? InvoicedAtUtc { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancellationReason { get; private set; }

    // --- lineitems ---

    /// <summary>
    /// Exposes line items as a read-only collection.
    /// External code CANNOT modify this collection directly.
    /// </summary>
    public IReadOnlyList<SaleLineItem> LineItems => _lineItems.AsReadOnly();



    // ==================================================================================================================================
    //                                                          CONSTRUCTORS
    // ==================================================================================================================================



#pragma warning disable CS8618
    /// <summary>
    /// Parameterless constructor required by Entity Framework Core.
    /// DO NOT use this in application code.
    /// </summary>
    private Sale() : base(Guid.Empty) { }
#pragma warning restore CS8618
    /// <summary>
    /// Private constructor used by the static factory method.
    /// This ensures all Sales are created through the Create() method,
    /// which enforces creation invariants and raises the appropriate event.
    /// </summary>
    private Sale
        (
        Guid buyerId,
        string buyerName,
        SaleNumber saleNumber,
        Guid createdByUserId,
        string createdByName
        ) : base(Guid.NewGuid())

    {

        if (buyerId == Guid.Empty) throw new DomainException("Buyer ID is required");
        if (string.IsNullOrWhiteSpace(buyerName)) throw new DomainException("Buyer name is required");
        if (saleNumber is null) throw new DomainException("Sale number is required");
        if (createdByUserId == Guid.Empty) throw new DomainException("ID of the User that created the sale (Created by ID) is required ");
        if (string.IsNullOrWhiteSpace(createdByName)) throw new DomainException("Name of the user that created the sale (created by name) is required");

        BuyerId = buyerId;
        BuyerName = buyerName;
        CreatedByUserId = createdByUserId;
        CreatedByName = createdByName;
        SaleNumber = saleNumber;

        Status = SaleStatus.Pending;      
        Total = Money.Zero("IRR");
        CreatedAtUtc = DateTime.UtcNow;

    }



    // ==================================================================================================================================
    //                                                          FACTORY METHOD
    // ==================================================================================================================================



    /// <summary>
    /// Creates a new Sale in Draft status.
    /// This is the ONLY way to create a Sale from application code.
    /// </summary>
    public static Sale Create
        (
        Guid BuyerId,
        string BuyerName,
        SaleNumber saleNumber,
        Guid createdByUserId,
        string createdByName
        )
    {
        var sale = new Sale
            (
            BuyerId,
            BuyerName,
            saleNumber,
            createdByUserId,
            createdByName
            );

        // Raise the domain event AFTER the object is fully constructed
        sale.AddDomainEvent
            (
            new SaleCreatedDomainEvent
            (
            sale.Id,            // FIXME  Aggregate root has this
            sale.BuyerId,
            sale.BuyerName,
            sale.SaleNumber,
            sale.CreatedAtUtc,
            sale.CreatedByUserId,
            sale.CreatedByName
            )
            );

        return sale;

    }



    // ==================================================================================================================================
    //                                                          LINE ITEM MANAGEMENT
    // ==================================================================================================================================



    public void AddLineItem
        (
    Guid productId,
    string productName,
    int quantity,
    Money unitPrice,
    string productCategory // for when we might want to limit how many they can buy per category as well
        )
    {
        EnsureProductQuantityValidity(quantity);
        EnsureProductPriceValidity(unitPrice);          // we gon use this thang for ... nothing. the product aggregate should protect itself

        // ------------------------------------------------------------------
        // CHECK FOR EXISTING LINE ITEM WITH THE SAME PRODUCT
        // ------------------------------------------------------------------
        var existingLine = _lineItems.FirstOrDefault(li => li.ProductId == productId);

        if (existingLine is not null)
        {
            AddExistingSaleLineItem(existingLine, productId, productName, quantity, unitPrice);
        }
        else
        {
            // This is a genuinely new product on this sale
            var lineNumber = _lineItems.Count + 1;

            var lineItem = new SaleLineItem
                (
                productId,
                productName,
                quantity,
                unitPrice,
                lineNumber,
               productCategory
                );

            _lineItems.Add(lineItem);

            Recalculatetotal();

            AddDomainEvent
                (
                new SaleLineItemAddedDomainEvent
                (
                Id,
                lineItem.Id,
                productId,
                productName,
                quantity,
                unitPrice,
                lineNumber
                )
                );
        }
    }



    /// <summary>
    /// Updates the quantity of an existing line item.
    /// </summary>
    public void UpdateLineItemQuantity(Guid lineItemId, int newQuantity)
    {
        EnsurePending();
        EnsureProductQuantityValidity(newQuantity);     // this is a guard that should be in the product itself, but we are doing it here for now



        // ------------------------------------------------------------------
        // FIND THE LINE ITEM
        // ------------------------------------------------------------------
        var lineItem = EnsureSaleLineItemExists(lineItemId);
        // ------------------------------------------------------------------
        // UPDATE AND RECALCULATE
        // ------------------------------------------------------------------
        lineItem.UpdateQuantity(newQuantity);
        Recalculatetotal();

        AddDomainEvent
            (
            new SaleLineItemUpdatedDomainEvent
            (
            Id,                         // this is the Base Entity Id which is inside Aggregate root (SaleId)
            lineItem.Id,
            lineItem.ProductId,
            lineItem.ProductName,
            newQuantity,
            lineItem.UnitPrice,
            lineItem.LineNumber
            )
            );
    }



    /// <summary>
    /// Removes a line item from the sale.
    /// </summary>
    public void RemoveLineItem(Guid lineItemId)
    {
        EnsurePending();

        // ------------------------------------------------------------------
        // FIND THE LINE ITEM       // the naming of this method has some issues. is it even a guard when it returns a value?
        // ------------------------------------------------------------------
        var lineItem = EnsureSaleLineItemExists(lineItemId);
        // ------------------------------------------------------------------
        // REMOVE AND RECALCULATE
        // ------------------------------------------------------------------
        var removedLineNumber = lineItem.LineNumber;
        _lineItems.Remove(lineItem);

        Recalculatetotal();

        AddDomainEvent
            (
            new SaleLineItemRemovedDomainEvent
            (
            Id,
            lineItem.Id,
            lineItem.ProductId,
            removedLineNumber
            )
            );
    }



    // ==================================================================================================================================
    //                                                          PRIVATE HELPERS
    // ==================================================================================================================================



    /// <summary>
    /// Core logic for adding quantity to an existing line item.
    /// Guards are repeated here so this method is safe to call from any context.
    /// </summary>
    private void AddExistingSaleLineItem(SaleLineItem existingLine, Guid productId, string productName, int quantity, Money unitPrice)
    {
        EnsurePending();
        EnsureProductPriceValidity(unitPrice);
        EnsureProductQuantityValidity(quantity);
        // Instead of adding a duplicate, increment the quantity of the existing line.
        // Business rationale: One line per product simplifies order fulfillment.
        var newQuantity = existingLine.Quantity + quantity;
        existingLine.UpdateQuantity(newQuantity);

        Recalculatetotal();

        // Raise an event reflecting the update
        AddDomainEvent
            (
            new SaleLineItemUpdatedDomainEvent
            (
            Id,
            existingLine.Id,
            productId,
            productName,
            newQuantity,
            existingLine.UnitPrice,
            existingLine.LineNumber
            )
            );
    }



    /// <summary>
    /// Recalculates Subtotal from line items.
    /// Called after every line item change.
    /// </summary>
    private void Recalculatetotal()
    {
        if (_lineItems.Count == 0)
        {
            Total = Money.Zero("IRR");
        }
        else
        {
            EnsureSaleLineItemExists(_lineItems[0].Id);
            var currency = _lineItems[0].UnitPrice.Currency;
            // aggregate function runs a function on every item and gives us a single value. we give it a seed (Money.Zero(currency))
            // and tell it that sum is the seed for the first step. now add all the money together and return as money
            // Subtotal = sum of gross totals (before any line discounts)
            Total = _lineItems.Aggregate
                (
                Money.Zero(currency), (sum, item) => sum + item.GrossTotal
                );
        }
    }





    // ==================================================================================================================================
    //                                                          CENTRALIZED GUARD METHODS
    // ==================================================================================================================================



    private void EnsureCancellable()
    {
        if (Status == SaleStatus.Invoiced)
        {
            throw new DomainException("Cannot cancel a sale that has already been invoiced. Issue a credit note instead.");
        }

        if (Status == SaleStatus.Cancelled)
        {
            throw new DomainException("This sale is already cancelled.");
        }
    }

    private static void EnsureProductQuantityValidity(int quantity)
    {
        if (quantity <= 0)
        {
            throw new DomainException("Quantity must be a positive number.");
        }
    }

    private static void EnsureProductPriceValidity(Money unitPrice)
    {
        if (unitPrice.Amount < 0)
        {
            throw new DomainException("Unit price cannot be negative.");
        }
    }

    private SaleLineItem EnsureSaleLineItemExists(Guid lineItemId)
    {
        var lineItem = _lineItems.FirstOrDefault(li => li.Id == lineItemId);

        if (lineItem is null)
        {
            throw new DomainException($"Line item with Id '{lineItemId}' was not found.");
        }
        return lineItem;
    }

    private void EnsureHasLineItems()
    {
        if (_lineItems.Count == 0)
        {
            throw new DomainException("Cannot submit a sale with no line items.");
        }
    }

    private void EnsureTotalIsPositive()
    {
        if (Total.Amount <= 0)
        {
            throw new DomainException("Cannot submit a sale with zero or negative total.");
        }
    }

    private static void EnsureReasonProvided(string reason, string message)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException(message);
    }

    private void EnsurePending()
    {
        if (Status != SaleStatus.Pending)
        {
            throw new DomainException(
                $"Only Pending sales can be approved. Current status: '{Status}'.");
        }
    }

    private void EnsureApproved()
    {
        if (Status != SaleStatus.Approved)
        {
            throw new DomainException($"Only Approved sales can perform this action. Current status: '{Status}'.");
        }
    }



    // ==================================================================================================================================
    //                                                          STATE TRANSITION METHODS
    // ==================================================================================================================================



    /// <summary>
    /// Approves the sale. This means the company has committed to fulfilling the order.
    /// Transitions from Pending to Approved.
    /// Discount and tax are frozen at this point since no further modifications are allowed.
    /// </summary>
    public void Approve(Guid approvedByUserId)
    {
        EnsurePending();
        EnsureTotalIsPositive();
        if (approvedByUserId == Guid.Empty) throw new DomainException("The ID of the user that approved the sale is required");

        Status = SaleStatus.Approved;
        ApprovedAtUtc = DateTime.UtcNow;
        ApprovedByUserId = approvedByUserId;

        AddDomainEvent(new SaleApprovedDomainEvent(Id, BuyerId, Total, approvedByUserId));
    }

    /// <summary>
    /// Cancels the sale. The sale is terminated and no further actions can be taken on it.
    /// Can be called from Draft, Pending, or Approved status.
    /// </summary>
    public void Cancel(Guid cancelledByUserId, string reason)
    {
        EnsureCancellable();
        EnsureReasonProvided(reason, "A cancellation reason is required.");
        if (cancelledByUserId == Guid.Empty) throw new DomainException("The ID of the user that cancelled the sale is required");

        Status = SaleStatus.Cancelled;
        CancelledAtUtc = DateTime.UtcNow;
        CancellationReason = reason;

        AddDomainEvent(new SaleCancelledDomainEvent(Id, cancelledByUserId, reason));
    }



    /// <summary>
    /// Marks the sale as invoiced. Transitions from Shipped to Invoiced.
    /// </summary>
    public void MarkAsInvoiced()
    {
        EnsurePending();

        Status = SaleStatus.Invoiced;
        InvoicedAtUtc = DateTime.UtcNow;
        AddDomainEvent(new SaleInvoicedDomainEvent(Id));
    }
}

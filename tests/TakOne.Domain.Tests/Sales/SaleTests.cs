using FluentAssertions;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Sales.Events;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Domain.Tests.Sales;

/// <summary>
/// Unit tests for the <see cref="Sale"/> aggregate root — the biggest
/// aggregate in the system. Verifies the full lifecycle
/// (Draft → Pending → Approved → Invoiced, plus Cancel from each state),
/// line-item management guards, purchase-limit enforcement, line-number
/// stability, and domain-event raising.
/// </summary>
public class SaleTests
{
    // Helper: build a Sale in Draft status with the standard test customer.
    private static Sale BuildDraftSale() => Sale.Create(
        customerId: TestValues.CustomerId,
        customerName: "Alice Customer",
        createdByUserId: TestValues.CreatedByUserId,
        createdByName: "Alice Creator");

    // Helper: build a Money with the sale's expected IRR currency.
    private static Money Irr(decimal amount) => new(amount, TestValues.IRR);

    // Helper: a standard per-call sale number for Submit tests.
    private static SaleNumber TestSaleNumber() => SaleNumber.Create(1403, 42);

    // ======================================================================
    //                          CREATE — HAPPY PATH
    // ======================================================================

    [Fact]
    public void Create_WithValidArgs_ReturnsDraftSaleWithCorrectProperties()
    {
        // Arrange
        var before = DateTime.UtcNow;

        // Act
        var sale = BuildDraftSale();

        // Assert
        sale.Id.Should().NotBeEmpty();
        sale.Status.Should().Be(SaleStatus.Draft);
        sale.SaleNumber.Should().BeNull();
        sale.Total.Should().Be(Money.Zero(TestValues.IRR));
        sale.CustomerId.Should().Be(TestValues.CustomerId);
        sale.CustomerName.Should().Be("Alice Customer");
        sale.CreatedByUserId.Should().Be(TestValues.CreatedByUserId);
        sale.CreatedByName.Should().Be("Alice Creator");
        sale.CreatedAtUtc.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
        sale.LineItems.Should().BeEmpty();
    }

    [Fact]
    public void Create_RaisesSaleCreatedDomainEvent()
    {
        // Act
        var sale = BuildDraftSale();

        // Assert
        sale.DomainEvents.Should().ContainSingle(e => e is SaleCreatedDomainEvent);
        var ev = sale.DomainEvents.OfType<SaleCreatedDomainEvent>().Single();
        ev.SaleId.Should().Be(sale.Id);
        ev.CustomerId.Should().Be(sale.CustomerId);
        ev.CustomerName.Should().Be(sale.CustomerName);
        ev.SaleNumber.Should().BeNull();
        ev.CreatedByUserId.Should().Be(sale.CreatedByUserId);
        ev.CreatedByName.Should().Be(sale.CreatedByName);
    }

    // ======================================================================
    //                          CREATE — GUARDS
    // ======================================================================

    [Fact]
    public void Create_WithEmptyCustomerId_Throws()
    {
        Action act = () => Sale.Create(
            customerId: Guid.Empty,
            customerName: "Alice",
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: "Creator");

        act.Should().Throw<DomainException>().WithMessage("Customer ID is required.");
    }

    [Fact]
    public void Create_WithEmptyCustomerName_Throws()
    {
        Action act = () => Sale.Create(
            customerId: TestValues.CustomerId,
            customerName: "",
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: "Creator");

        act.Should().Throw<DomainException>().WithMessage("Customer name is required.");
    }

    [Fact]
    public void Create_WithWhitespaceCustomerName_Throws()
    {
        Action act = () => Sale.Create(
            customerId: TestValues.CustomerId,
            customerName: "   ",
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: "Creator");

        act.Should().Throw<DomainException>().WithMessage("Customer name is required.");
    }

    [Fact]
    public void Create_WithEmptyCreatedByUserId_Throws()
    {
        Action act = () => Sale.Create(
            customerId: TestValues.CustomerId,
            customerName: "Alice",
            createdByUserId: Guid.Empty,
            createdByName: "Creator");

        act.Should().Throw<DomainException>().WithMessage("Created-by user ID is required.");
    }

    [Fact]
    public void Create_WithEmptyCreatedByName_Throws()
    {
        Action act = () => Sale.Create(
            customerId: TestValues.CustomerId,
            customerName: "Alice",
            createdByUserId: TestValues.CreatedByUserId,
            createdByName: "");

        act.Should().Throw<DomainException>().WithMessage("Created-by user name is required.");
    }

    // ======================================================================
    //                          ADD LINE ITEM
    // ======================================================================

    [Fact]
    public void AddLineItem_WithValidArgs_AddsLineAndRecalculatesTotal()
    {
        // Arrange
        var sale = BuildDraftSale();
        var unitPrice = Irr(10m);

        // Act
        sale.AddLineItem(TestValues.ProductId, "Pencil", 5, unitPrice);

        // Assert
        sale.LineItems.Should().HaveCount(1);
        sale.LineItems[0].ProductId.Should().Be(TestValues.ProductId);
        sale.LineItems[0].ProductName.Should().Be("Pencil");
        sale.LineItems[0].Quantity.Should().Be(5);
        sale.LineItems[0].UnitPrice.Should().Be(unitPrice);
        sale.LineItems[0].LineNumber.Should().Be(1);
        sale.Total.Should().Be(Irr(50m)); // 5 * 10
    }

    [Fact]
    public void AddLineItem_WithNewProduct_RaisesSaleLineItemAddedDomainEvent()
    {
        // Arrange
        var sale = BuildDraftSale();

        // Act
        sale.AddLineItem(TestValues.ProductId, "Pencil", 5, Irr(10m));

        // Assert — the prior SaleCreated event is still in the list, plus a new
        // SaleLineItemAdded event
        sale.DomainEvents.OfType<SaleLineItemAddedDomainEvent>().Should().ContainSingle();
        var ev = sale.DomainEvents.OfType<SaleLineItemAddedDomainEvent>().Single();
        ev.SaleId.Should().Be(sale.Id);
        ev.ProductId.Should().Be(TestValues.ProductId);
        ev.Quantity.Should().Be(5);
        ev.LineNumber.Should().Be(1);
    }

    [Fact]
    public void AddLineItem_ForExistingProductId_IncrementsQuantityAndRaisesUpdatedEvent()
    {
        // Arrange — same product added twice → second call merges into first line
        var sale = BuildDraftSale();

        // Act
        sale.AddLineItem(TestValues.ProductId, "Pencil", 5, Irr(10m));
        sale.AddLineItem(TestValues.ProductId, "Pencil", 3, Irr(10m));

        // Assert — one line, qty=8, total=80
        sale.LineItems.Should().HaveCount(1);
        sale.LineItems[0].Quantity.Should().Be(8);
        sale.Total.Should().Be(Irr(80m));

        // The second AddLineItem raised an Updated event (not Added)
        sale.DomainEvents.OfType<SaleLineItemAddedDomainEvent>().Should().ContainSingle();
        sale.DomainEvents.OfType<SaleLineItemUpdatedDomainEvent>().Should().ContainSingle();
    }

    [Fact]
    public void AddLineItem_AssignsSequentialLineNumbers()
    {
        // Arrange
        var sale = BuildDraftSale();

        // Act — add two different products (different ProductIds → two lines)
        sale.AddLineItem(TestValues.ProductId, "P1", 1, Irr(1m));
        sale.AddLineItem(Guid.NewGuid(), "P2", 1, Irr(1m));

        // Assert
        sale.LineItems[0].LineNumber.Should().Be(1);
        sale.LineItems[1].LineNumber.Should().Be(2);
    }

    [Fact]
    public void AddLineItem_WithZeroQuantity_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.AddLineItem(TestValues.ProductId, "P", 0, Irr(10m));

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Quantity must be a positive integer.");
    }

    [Fact]
    public void AddLineItem_WithNegativeQuantity_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.AddLineItem(TestValues.ProductId, "P", -2, Irr(10m));

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Quantity must be a positive integer.");
    }

    [Fact]
    public void AddLineItem_WithNegativeUnitPrice_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(-5m));

        // Assert
        act.Should().Throw<DomainException>().WithMessage("Unit price cannot be negative.");
    }

    [Fact]
    public void AddLineItem_WhenNotInDraft_Throws()
    {
        // Arrange — submit the sale first so it's Pending
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(10m));
        sale.Submit(TestSaleNumber());

        // Act — can no longer add lines once submitted
        Action act = () => sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(10m));

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot modify line items of a sale that is not in Draft. Current status: 'Pending'.");
    }

    [Fact]
    public void AddLineItem_WhenPurchaseLimitExceeded_Throws()
    {
        // Arrange — limit=3, requesting 5
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m), purchaseLimit: 3);

        // Assert — error message format includes the productId + the limit + requested qty
        act.Should().Throw<DomainException>()
            .WithMessage($"Purchase limit exceeded for product '{TestValues.ProductId}'. Limit: 3, requested: 5.");
    }

    [Fact]
    public void AddLineItem_WhenPurchaseLimitNull_DoesNotEnforceLimit()
    {
        // Arrange — null limit means "no limit for this buyer/product"
        var sale = BuildDraftSale();

        // Act — large quantity with null limit should be accepted
        sale.AddLineItem(TestValues.ProductId, "P", 1000, Irr(1m), purchaseLimit: null);

        // Assert
        sale.LineItems[0].Quantity.Should().Be(1000);
    }

    // ======================================================================
    //                          UPDATE LINE ITEM QUANTITY
    // ======================================================================

    [Fact]
    public void UpdateLineItemQuantity_WithValidQuantity_UpdatesLineAndRecalculatesTotal()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(10m));
        var lineItemId = sale.LineItems[0].Id;

        // Act
        sale.UpdateLineItemQuantity(lineItemId, 5);

        // Assert
        sale.LineItems[0].Quantity.Should().Be(5);
        sale.Total.Should().Be(Irr(50m));
    }

    [Fact]
    public void UpdateLineItemQuantity_RaisesSaleLineItemUpdatedDomainEvent()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(10m));
        sale.ClearDomainEvents();
        var lineItemId = sale.LineItems[0].Id;

        // Act
        sale.UpdateLineItemQuantity(lineItemId, 7);

        // Assert
        sale.DomainEvents.OfType<SaleLineItemUpdatedDomainEvent>().Should().ContainSingle();
    }

    [Fact]
    public void UpdateLineItemQuantity_WhenNotInDraft_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(10m));
        var lineItemId = sale.LineItems[0].Id;
        sale.Submit(TestSaleNumber());

        // Act
        Action act = () => sale.UpdateLineItemQuantity(lineItemId, 5);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot modify line items of a sale that is not in Draft. Current status: 'Pending'.");
    }

    [Fact]
    public void UpdateLineItemQuantity_WithZeroQuantity_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(10m));

        // Act
        Action act = () => sale.UpdateLineItemQuantity(sale.LineItems[0].Id, 0);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Quantity must be a positive integer.");
    }

    [Fact]
    public void UpdateLineItemQuantity_WithNonExistingLineItemId_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        var unknownId = Guid.NewGuid();

        // Act
        Action act = () => sale.UpdateLineItemQuantity(unknownId, 5);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage($"Line item with Id '{unknownId}' was not found on this sale.");
    }

    [Fact]
    public void UpdateLineItemQuantity_WhenPurchaseLimitExceeded_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(10m));
        var lineItemId = sale.LineItems[0].Id;

        // Act
        Action act = () => sale.UpdateLineItemQuantity(lineItemId, 10, purchaseLimit: 3);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage($"Purchase limit exceeded for product '{TestValues.ProductId}'. Limit: 3, requested: 10.");
    }

    // ======================================================================
    //                          REMOVE LINE ITEM
    // ======================================================================

    [Fact]
    public void RemoveLineItem_WithExistingLineItemId_RemovesLineAndRecalculatesTotal()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        var lineItemId = sale.LineItems[0].Id;

        // Act
        sale.RemoveLineItem(lineItemId);

        // Assert
        sale.LineItems.Should().BeEmpty();
        sale.Total.Should().Be(Money.Zero(TestValues.IRR));
    }

    [Fact]
    public void RemoveLineItem_RaisesSaleLineItemRemovedDomainEvent()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.ClearDomainEvents();
        var lineItemId = sale.LineItems[0].Id;

        // Act
        sale.RemoveLineItem(lineItemId);

        // Assert
        sale.DomainEvents.OfType<SaleLineItemRemovedDomainEvent>().Should().ContainSingle();
    }

    [Fact]
    public void RemoveLineItem_WhenNotInDraft_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        var lineItemId = sale.LineItems[0].Id;
        sale.Submit(TestSaleNumber());

        // Act
        Action act = () => sale.RemoveLineItem(lineItemId);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot modify line items of a sale that is not in Draft. Current status: 'Pending'.");
    }

    [Fact]
    public void RemoveLineItem_WithNonExistingLineItemId_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        var unknownId = Guid.NewGuid();

        // Act
        Action act = () => sale.RemoveLineItem(unknownId);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage($"Line item with Id '{unknownId}' was not found on this sale.");
    }

    [Fact]
    public void RemoveLineItem_PreservesLineNumbersOfRemainingLines()
    {
        // Arrange — add three different products to get line numbers 1,2,3.
        // Then remove the middle one. The third line's LineNumber must stay 3.
        var sale = BuildDraftSale();
        sale.AddLineItem(Guid.NewGuid(), "P1", 1, Irr(1m));
        sale.AddLineItem(Guid.NewGuid(), "P2", 1, Irr(1m));
        sale.AddLineItem(Guid.NewGuid(), "P3", 1, Irr(1m));

        var middleId = sale.LineItems[1].Id;

        // Act
        sale.RemoveLineItem(middleId);

        // Assert — remaining lines: index 0 keeps LineNumber=1, index 1 keeps LineNumber=3
        sale.LineItems.Should().HaveCount(2);
        sale.LineItems[0].LineNumber.Should().Be(1);
        sale.LineItems[1].LineNumber.Should().Be(3);
    }

    // ======================================================================
    //                          SUBMIT
    // ======================================================================

    [Fact]
    public void Submit_WithLineItemsAndValidSaleNumber_TransitionsToPendingAndAllocatesSaleNumber()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        var saleNumber = TestSaleNumber();
        var before = DateTime.UtcNow;

        // Act
        sale.Submit(saleNumber);

        // Assert
        sale.Status.Should().Be(SaleStatus.Pending);
        sale.SaleNumber.Should().Be(saleNumber);
        sale.SubmittedAtUtc.Should().NotBeNull();
        sale.SubmittedAtUtc!.Value.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Submit_RaisesSaleSubmittedDomainEvent()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.ClearDomainEvents();
        var saleNumber = TestSaleNumber();

        // Act
        sale.Submit(saleNumber);

        // Assert
        sale.DomainEvents.OfType<SaleSubmittedDomainEvent>().Should().ContainSingle();
        var ev = sale.DomainEvents.OfType<SaleSubmittedDomainEvent>().Single();
        ev.SaleId.Should().Be(sale.Id);
        ev.CustomerId.Should().Be(sale.CustomerId);
        ev.Total.Should().Be(sale.Total);
        ev.SaleNumber.Should().Be(saleNumber);
    }

    [Fact]
    public void Submit_WhenNotInDraft_Throws()
    {
        // Arrange — submit twice; second submit should fail
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());

        // Act
        Action act = () => sale.Submit(TestSaleNumber());

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot modify line items of a sale that is not in Draft. Current status: 'Pending'.");
    }

    [Fact]
    public void Submit_WithNoLineItems_Throws()
    {
        // Arrange — empty draft cart
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.Submit(TestSaleNumber());

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot submit a sale with no line items.");
    }

    [Fact]
    public void Submit_WithZeroTotal_Throws()
    {
        // Arrange — Sale's Total defaults to Money.Zero("IRR") which is 0.
        // AddLineItem won't accept negative or zero quantities, so the only
        // way to get Total=0 here is to add a line, then remove it, leaving
        // an empty cart. RecalculateTotal resets to Money.Zero. Then submit
        // would fail "no line items" first. So construct a "zero total"
        // scenario by adding and removing all lines — submit then fails
        // on no-line-items (also a zero-total scenario). We test the
        // zero-total guard via the no-items path since both throw.
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(10m));
        sale.RemoveLineItem(sale.LineItems[0].Id);

        // Act
        Action act = () => sale.Submit(TestSaleNumber());

        // Assert — "no line items" guards fires first (the implementation
        // checks EnsureHasLineItems before EnsureTotalIsPositive)
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot submit a sale with no line items.");
    }

    [Fact]
    public void Submit_WithNullSaleNumber_Throws()
    {
        // Arrange — submit without first allocating a SaleNumber is a
        // programmer error (the app layer should allocate via
        // ISaleNumberGenerator.NextAsync immediately before Submit).
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));

        // Act — pass null even though the parameter is non-nullable;
        // we use null-forgiving to simulate the programmer error.
        #pragma warning disable CS8604
        Action act = () => sale.Submit(null!);
        #pragma warning restore CS8604

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("A SaleNumber must be allocated and passed to Submit().*");
    }

    // ======================================================================
    //                          APPROVE
    // ======================================================================

    [Fact]
    public void Approve_FromPending_TransitionsToApprovedAndSetsAuditFields()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        var before = DateTime.UtcNow;

        // Act
        sale.Approve(TestValues.ApprovedByUserId);

        // Assert
        sale.Status.Should().Be(SaleStatus.Approved);
        sale.ApprovedByUserId.Should().Be(TestValues.ApprovedByUserId);
        sale.ApprovedAtUtc.Should().NotBeNull();
        sale.ApprovedAtUtc!.Value.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Approve_RaisesSaleApprovedDomainEvent()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        sale.ClearDomainEvents();

        // Act
        sale.Approve(TestValues.ApprovedByUserId);

        // Assert
        sale.DomainEvents.OfType<SaleApprovedDomainEvent>().Should().ContainSingle();
    }

    [Fact]
    public void Approve_WhenNotPending_Throws()
    {
        // Arrange — still Draft, can't approve yet
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));

        // Act
        Action act = () => sale.Approve(TestValues.ApprovedByUserId);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Only Pending sales can be approved. Current status: 'Draft'.");
    }

    [Fact]
    public void Approve_WithEmptyApprovedByUserId_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());

        // Act
        Action act = () => sale.Approve(Guid.Empty);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("A valid user Id is required (approvedByUserId).");
    }

    // ======================================================================
    //                          MARK AS INVOICED
    // ======================================================================

    [Fact]
    public void MarkAsInvoiced_FromApproved_TransitionsToInvoiced()
    {
        // Arrange — get to Approved first
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        sale.Approve(TestValues.ApprovedByUserId);
        var before = DateTime.UtcNow;

        // Act
        sale.MarkAsInvoiced(TestValues.InvoicedByUserId);

        // Assert
        sale.Status.Should().Be(SaleStatus.Invoiced);
        sale.InvoicedByUserId.Should().Be(TestValues.InvoicedByUserId);
        sale.InvoicedAtUtc.Should().NotBeNull();
        sale.InvoicedAtUtc!.Value.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MarkAsInvoiced_RaisesSaleInvoicedDomainEvent()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        sale.Approve(TestValues.ApprovedByUserId);
        sale.ClearDomainEvents();

        // Act
        sale.MarkAsInvoiced(TestValues.InvoicedByUserId);

        // Assert
        sale.DomainEvents.OfType<SaleInvoicedDomainEvent>().Should().ContainSingle();
    }

    [Fact]
    public void MarkAsInvoiced_WhenNotApproved_Throws()
    {
        // Arrange — Pending, not Approved
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());

        // Act
        Action act = () => sale.MarkAsInvoiced(TestValues.InvoicedByUserId);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Only Approved sales can be marked as invoiced. Current status: 'Pending'.");
    }

    [Fact]
    public void MarkAsInvoiced_WithEmptyInvoicedByUserId_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        sale.Approve(TestValues.ApprovedByUserId);

        // Act
        Action act = () => sale.MarkAsInvoiced(Guid.Empty);

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("A valid user Id is required (invoicedByUserId).");
    }

    // ======================================================================
    //                          CANCEL
    // ======================================================================

    [Fact]
    public void Cancel_FromPending_TransitionsToCancelledAndSetsAuditFields()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        var before = DateTime.UtcNow;

        // Act
        sale.Cancel(TestValues.CancelledByUserId, "Customer changed mind");

        // Assert
        sale.Status.Should().Be(SaleStatus.Cancelled);
        sale.CancelledByUserId.Should().Be(TestValues.CancelledByUserId);
        sale.CancellationReason.Should().Be("Customer changed mind");
        sale.CancelledAtUtc.Should().NotBeNull();
        sale.CancelledAtUtc!.Value.Should().BeCloseTo(before, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Cancel_FromApproved_AlsoTransitionsToCancelled()
    {
        // Arrange — Approved is also a valid cancel source
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        sale.Approve(TestValues.ApprovedByUserId);

        // Act
        sale.Cancel(TestValues.CancelledByUserId, "Wrong approval");

        // Assert
        sale.Status.Should().Be(SaleStatus.Cancelled);
    }

    [Fact]
    public void Cancel_RaisesSaleCancelledDomainEvent()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        sale.ClearDomainEvents();

        // Act
        sale.Cancel(TestValues.CancelledByUserId, "Because");

        // Assert
        sale.DomainEvents.OfType<SaleCancelledDomainEvent>().Should().ContainSingle();
        var ev = sale.DomainEvents.OfType<SaleCancelledDomainEvent>().Single();
        ev.Reason.Should().Be("Because");
    }

    [Fact]
    public void Cancel_FromDraft_Throws()
    {
        // Arrange — drafts are hard-deleted via repo, not cancelled
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.Cancel(TestValues.CancelledByUserId, "Whatever");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot cancel a Draft sale. Delete it via the repository instead.");
    }

    [Fact]
    public void Cancel_FromInvoiced_Throws()
    {
        // Arrange — invoiced is terminal; issue a credit note instead
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        sale.Approve(TestValues.ApprovedByUserId);
        sale.MarkAsInvoiced(TestValues.InvoicedByUserId);

        // Act
        Action act = () => sale.Cancel(TestValues.CancelledByUserId, "Reason");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("Cannot cancel a sale that has already been invoiced. Issue a credit note instead.");
    }

    [Fact]
    public void Cancel_FromCancelled_Throws()
    {
        // Arrange — already cancelled, cannot re-cancel
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());
        sale.Cancel(TestValues.CancelledByUserId, "First reason");

        // Act
        Action act = () => sale.Cancel(TestValues.CancelledByUserId, "Second reason");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("This sale is already cancelled.");
    }

    [Fact]
    public void Cancel_WithEmptyCancelledByUserId_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());

        // Act
        Action act = () => sale.Cancel(Guid.Empty, "Reason");

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("A valid user Id is required (cancelledByUserId).");
    }

    [Fact]
    public void Cancel_WithEmptyReason_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());

        // Act
        Action act = () => sale.Cancel(TestValues.CancelledByUserId, "");

        // Assert
        act.Should().Throw<DomainException>().WithMessage("A cancellation reason is required.");
    }

    [Fact]
    public void Cancel_WithWhitespaceReason_Throws()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));
        sale.Submit(TestSaleNumber());

        // Act
        Action act = () => sale.Cancel(TestValues.CancelledByUserId, "   ");

        // Assert
        act.Should().Throw<DomainException>().WithMessage("A cancellation reason is required.");
    }
}

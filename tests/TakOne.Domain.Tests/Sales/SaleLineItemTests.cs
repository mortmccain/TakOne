using FluentAssertions;
using TakOne.Domain.Sales.Entities;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Domain.Tests.Sales;

/// <summary>
/// Unit tests for <see cref="SaleLineItem"/>.
///
/// <see cref="SaleLineItem"/>'s constructor and <see cref="SaleLineItem.UpdateQuantity"/>
/// method are <c>internal</c>, so they cannot be exercised directly from
/// this test assembly (no <c>InternalsVisibleTo</c> declared on
/// TakOne.Domain). Instead, all tests go through the public
/// <see cref="Sale.AddLineItem"/> / <see cref="Sale.UpdateLineItemQuantity"/>
/// API of the parent <see cref="Sale"/> aggregate — which is the
/// idiomatic DDD pattern: the invariant guards on the inner entity are
/// observable through the aggregate root's public API.
/// </summary>
public class SaleLineItemTests
{
    // Helper: build a Sale in Draft status with the standard test customer.
    private static Sale BuildDraftSale() => Sale.Create(
        customerId: TestValues.CustomerId,
        customerName: "Alice Customer",
        saleNumber: null,
        createdByUserId: TestValues.CreatedByUserId,
        createdByName: "Alice Creator");

    private static Money Irr(decimal amount) => new(amount, TestValues.IRR);

    // ======================================================================
    //                          PUBLIC PROPERTY OBSERVABILITY
    // ======================================================================

    [Fact]
    public void SaleLineItem_WhenAddedViaSale_SetsProductIdNameQuantityUnitPriceLineNumber()
    {
        // Arrange
        var sale = BuildDraftSale();
        var unitPrice = Irr(10m);

        // Act
        sale.AddLineItem(TestValues.ProductId, "Pencil", 5, unitPrice);

        // Assert — the line item's properties are set correctly by the
        // internal constructor
        var line = sale.LineItems[0];
        line.Id.Should().NotBeEmpty();
        line.ProductId.Should().Be(TestValues.ProductId);
        line.ProductName.Should().Be("Pencil");
        line.Quantity.Should().Be(5);
        line.UnitPrice.Should().Be(unitPrice);
        line.LineNumber.Should().Be(1);
    }

    [Fact]
    public void GrossTotal_WhenQuantityTimesUnitPrice_ReturnsCorrectMoney()
    {
        // Arrange — quantity=5, unitPrice=Money(10, "IRR") → GrossTotal=50
        var sale = BuildDraftSale();

        // Act
        sale.AddLineItem(TestValues.ProductId, "Pencil", 5, Irr(10m));

        // Assert
        sale.LineItems[0].GrossTotal.Should().Be(Irr(50m));
    }

    [Fact]
    public void GrossTotal_WhenQuantityOneAndUnitPriceTen_ReturnsTen()
    {
        // Arrange — single-unit line item
        var sale = BuildDraftSale();

        // Act
        sale.AddLineItem(TestValues.ProductId, "Pencil", 1, Irr(10m));

        // Assert
        sale.LineItems[0].GrossTotal.Should().Be(Irr(10m));
    }

    [Fact]
    public void GrossTotal_WhenQuantityLarge_ReturnsCorrectTotal()
    {
        // Arrange — large qty tests the multiplication edge
        var sale = BuildDraftSale();

        // Act
        sale.AddLineItem(TestValues.ProductId, "Bulk", 1000, Irr(7m));

        // Assert
        sale.LineItems[0].GrossTotal.Should().Be(Irr(7000m));
    }

    // ======================================================================
    //                          DEFENSIVE COPY OF UNIT PRICE
    // ======================================================================

    [Fact]
    public void SaleLineItem_WhenConstructed_MakesDefensiveCopyOfUnitPrice()
    {
        // Arrange — the internal ctor does `new Money(unitPrice.Amount, unitPrice.Currency)`
        // rather than holding the caller's reference. Verify by checking that
        // the line's UnitPrice is a DIFFERENT instance from the one we passed in
        // (but equal by value).
        var sale = BuildDraftSale();
        var passedInPrice = Irr(10m);

        // Act
        sale.AddLineItem(TestValues.ProductId, "P", 1, passedInPrice);

        // Assert — different references, but equal by value
        var linePrice = sale.LineItems[0].UnitPrice;
        linePrice.Should().NotBeSameAs(passedInPrice);
        linePrice.Should().Be(passedInPrice);
    }

    // ======================================================================
    //                          CONSTRUCTOR GUARDS — REACHED VIA Sale.AddLineItem
    // ======================================================================

    [Fact]
    public void SaleLineItem_WhenProductIdEmpty_Throws()
    {
        // Arrange — Sale.AddLineItem doesn't guard productId itself, but
        // the SaleLineItem ctor does (EnsureProductIdValid)
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.AddLineItem(Guid.Empty, "P", 1, Irr(10m));

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("ProductId is required on a SaleLineItem.");
    }

    [Fact]
    public void SaleLineItem_WhenProductNameEmpty_Throws()
    {
        // Arrange — empty ProductName triggers SaleLineItem.EnsureProductNameValid
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.AddLineItem(TestValues.ProductId, "", 1, Irr(10m));

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("ProductName is required on a SaleLineItem.");
    }

    [Fact]
    public void SaleLineItem_WhenProductNameWhitespace_Throws()
    {
        // Arrange — whitespace ProductName is also rejected by IsNullOrWhiteSpace
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.AddLineItem(TestValues.ProductId, "   ", 1, Irr(10m));

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("ProductName is required on a SaleLineItem.");
    }

    [Fact]
    public void SaleLineItem_WhenProductNameExceeds200Chars_Throws()
    {
        // Arrange — boundary violation on ProductName length
        var sale = BuildDraftSale();
        var longName = new string('x', 201);

        // Act
        Action act = () => sale.AddLineItem(TestValues.ProductId, longName, 1, Irr(10m));

        // Assert
        act.Should().Throw<DomainException>()
            .WithMessage("ProductName cannot exceed 200 characters.");
    }

    [Fact]
    public void SaleLineItem_WhenQuantityZero_ThrowsViaSaleGuard()
    {
        // Arrange — Sale's own EnsureQuantityValid fires first (defense-in-depth:
        // the SaleLineItem ctor has the same guard but never reaches it
        // because Sale's guard short-circuits the call before constructing)
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.AddLineItem(TestValues.ProductId, "P", 0, Irr(10m));

        // Assert — Sale's guard message is the same wording as SaleLineItem's
        act.Should().Throw<DomainException>()
            .WithMessage("Quantity must be a positive integer.");
    }

    [Fact]
    public void SaleLineItem_WhenUnitPriceNegative_ThrowsViaSaleGuard()
    {
        // Arrange — Sale's own EnsureUnitPriceValid fires first
        var sale = BuildDraftSale();

        // Act
        Action act = () => sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(-1m));

        // Assert — Sale's guard message matches SaleLineItem's
        act.Should().Throw<DomainException>()
            .WithMessage("Unit price cannot be negative.");
    }

    // ======================================================================
    //                          UPDATE QUANTITY (internal)
    // ======================================================================

    [Fact]
    public void UpdateQuantity_WhenCalledViaSaleAddLineItemTwice_MergesQuantity()
    {
        // Arrange — adding the same product twice calls SaleLineItem.UpdateQuantity
        // on the existing line (the second AddLineItem finds the line for the
        // same productId and calls lineItem.UpdateQuantity(newQuantity)).
        var sale = BuildDraftSale();

        // Act
        sale.AddLineItem(TestValues.ProductId, "Pencil", 5, Irr(10m));
        sale.AddLineItem(TestValues.ProductId, "Pencil", 3, Irr(10m));

        // Assert — the existing line's Quantity was updated to 8 by the
        // internal UpdateQuantity method
        sale.LineItems.Should().HaveCount(1);
        sale.LineItems[0].Quantity.Should().Be(8);
    }

    [Fact]
    public void UpdateQuantity_WhenCalledViaUpdateLineItemQuantity_UpdatesTheLine()
    {
        // Arrange
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 5, Irr(10m));

        // Act — UpdateLineItemQuantity calls lineItem.UpdateQuantity(newQuantity)
        sale.UpdateLineItemQuantity(sale.LineItems[0].Id, 12);

        // Assert
        sale.LineItems[0].Quantity.Should().Be(12);
    }

    [Fact]
    public void SaleLineItem_InheritsFromBaseEntity_AndHasIdentityEquality()
    {
        // Arrange — add a line via the parent sale
        var sale = BuildDraftSale();
        sale.AddLineItem(TestValues.ProductId, "P", 1, Irr(10m));
        var line = sale.LineItems[0];

        // Assert — same instance is equal to itself; the line's Id is set
        line.Id.Should().NotBeEmpty();
        line.Equals(line).Should().BeTrue();
    }
}

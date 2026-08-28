using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.Infrastructure.Services;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for the Sale aggregate's state-machine persistence.
/// Each test drives the aggregate through one or more state transitions
/// (Draft → Pending → Approved → Invoiced / Cancelled), persists via the
/// real <see cref="UnitOfWork"/>, and reloads the Sale from a fresh
/// DbContext-equivalent (change-tracker clear) to assert on persisted
/// state — catching any change-tracker-vs-DB drift the mock-heavy unit
/// tests cannot detect.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THE MONEY CURRENCY IS "IRR" BY DEFAULT:</b> the
/// <see cref="Sale"/> constructor hard-codes <c>Total = Money.Zero("IRR")</c>
/// for fresh drafts. After the first <c>AddLineItem</c>, the
/// <c>RecalculateTotal()</c> method takes the line's currency. The
/// "new draft has zero total" test therefore asserts
/// <c>Total.Currency == "IRR"</c> (the constructor's default), not a
/// command-supplied currency (the Sale factory has no currency parameter).
/// </para>
/// <para>
/// <b>SALENUMBER FORMAT:</b> <see cref="SaleNumber.Value"/> returns the
/// canonical display string <c>INT-{PersianDigits(Year)}-{PersianDigits(Sequence:D8)}</c>
/// (e.g. <c>INT-۱۴۰۳-۰۰۰۰۰۰۰۱</c>). Persian digits are intentional —
/// the system is built for Iranian users. Tests assert the exact string.
/// </para>
/// </remarks>
public class SaleStateMachineIntegrationTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private const string CustomerName = "John Customer";
    private const string CreatedByName = "Test Staff";

    private static async Task<(
        ISaleRepository saleRepo,
        IUnitOfWork unitOfWork,
        ApplicationDbContext db)>
        BuildWiredCollaboratorsAsync()
    {
        var db = await SqliteTestDbFactory.CreateAsync();
        var saleRepo = new SaleRepository(db);
        var unitOfWork = new UnitOfWork(db);
        return (saleRepo, unitOfWork, db);
    }

    // ── Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSale_NewDraftHasStatusDraftAndZeroTotal()
    {
        // Arrange
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var beforeUtc = DateTime.UtcNow;

            // Act — create a fresh Draft Sale. saleNumber is intentionally
            // null (B2 deferred-allocation design).
            var sale = Sale.Create(
                customerId: TestValues.CustomerId,
                customerName: CustomerName,
                createdByUserId: TestValues.CreatedByUserId,
                createdByName: CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert — reload from DB and verify the persisted state.
            db.ChangeTracker.Clear();
            var reloaded = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            reloaded.Should().NotBeNull();
            reloaded!.Status.Should().Be(SaleStatus.Draft);
            reloaded.CustomerId.Should().Be(TestValues.CustomerId);
            reloaded.CustomerName.Should().Be(CustomerName);
            reloaded.CreatedByUserId.Should().Be(TestValues.CreatedByUserId);
            reloaded.CreatedByName.Should().Be(CreatedByName);
            reloaded.Total.Amount.Should().Be(0m);
            // Total.Currency is hard-coded to "IRR" by the Sale ctor —
            // see class-level doc.
            reloaded.Total.Currency.Should().Be(TestValues.IRR);
            reloaded.SaleNumber.Should().BeNull();
            reloaded.LineItems.Should().BeEmpty();
            reloaded.CreatedAtUtc.Should().BeCloseTo(beforeUtc, TimeSpan.FromSeconds(5));
            reloaded.SubmittedAtUtc.Should().BeNull();
        }
    }

    [Fact]
    public async Task CreateSale_AddLineItem_TotalReflectsLineGross()
    {
        // Arrange
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            // Act — reload, add a line, persist.
            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.AddLineItem(
                productId: TestValues.ProductId,
                productName: "Apple",
                quantity: 2,
                unitPrice: new Money(1.50m, TestValues.USD));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert — Total = 2 × 1.50 = 3.00 USD.
            db.ChangeTracker.Clear();
            var reloaded = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            reloaded!.LineItems.Should().HaveCount(1);
            reloaded.Total.Amount.Should().Be(3.00m);
            reloaded.Total.Currency.Should().Be(TestValues.USD);
        }
    }

    [Fact]
    public async Task CreateSale_AddThreeLineItems_TotalIsSum()
    {
        // Arrange
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            // Act — add three distinct-product lines.
            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.AddLineItem(TestValues.ProductId, "Apple", 2, new Money(1.00m, TestValues.USD));
            sale.AddLineItem(Guid.NewGuid(), "Banana", 3, new Money(0.50m, TestValues.USD));
            sale.AddLineItem(Guid.NewGuid(), "Cherry", 1, new Money(5.00m, TestValues.USD));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert — Total = 2 + 1.50 + 5.00 = 8.50 USD.
            // 2×1.00 + 3×0.50 + 1×5.00 = 2.00 + 1.50 + 5.00 = 8.50
            db.ChangeTracker.Clear();
            var reloaded = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            reloaded!.LineItems.Should().HaveCount(3);
            reloaded.Total.Amount.Should().Be(8.50m);
            reloaded.Total.Currency.Should().Be(TestValues.USD);
        }
    }

    [Fact]
    public async Task CreateSale_UpdateLineItemQuantity_TotalRecomputes()
    {
        // Arrange
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.AddLineItem(TestValues.ProductId, "Apple", 2, new Money(1.00m, TestValues.USD));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            // Act — update the line's quantity from 2 to 5.
            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            var lineItemId = sale!.LineItems.First().Id;
            sale.UpdateLineItemQuantity(lineItemId, 5);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert — line Quantity=5, line GrossTotal=5.00, Sale Total=5.00.
            db.ChangeTracker.Clear();
            var reloaded = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            reloaded!.LineItems.First().Quantity.Should().Be(5);
            reloaded.LineItems.First().GrossTotal.Amount.Should().Be(5.00m);
            reloaded.Total.Amount.Should().Be(5.00m);
        }
    }

    [Fact]
    public async Task CreateSale_RemoveLineItem_TotalDropsByLineGross()
    {
        // Arrange
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.AddLineItem(TestValues.ProductId, "Apple", 2, new Money(2.00m, TestValues.USD));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            // Act — remove the only line.
            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            var lineItemId = sale!.LineItems.First().Id;
            sale.RemoveLineItem(lineItemId);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert — line items empty, Total back to 0 (with line's currency
            // preserved — Money.Zero(currency) where currency was the line's
            // currency before the RecalculateTotal set it; with no lines,
            // RecalculateTotal resets to Money.Zero("IRR")).
            db.ChangeTracker.Clear();
            var reloaded = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            reloaded!.LineItems.Should().BeEmpty();
            reloaded.Total.Amount.Should().Be(0m);
        }
    }

    [Fact]
    public async Task CreateSale_Submit_StatusPendingAndSaleNumberSet()
    {
        // Arrange
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.AddLineItem(TestValues.ProductId, "Apple", 1, new Money(1.00m, TestValues.USD));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            var beforeSubmit = DateTime.UtcNow;

            // Act — Submit with a freshly-allocated SaleNumber.
            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.Submit(SaleNumber.Create(1403, 1));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert
            db.ChangeTracker.Clear();
            var reloaded = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            reloaded!.Status.Should().Be(SaleStatus.Pending);
            reloaded.SaleNumber.Should().NotBeNull();
            // SaleNumber.Value returns the canonical Persian-digit display
            // string — see class-level doc.
            reloaded.SaleNumber!.Value.Should().Be("INT-۱۴۰۳-۰۰۰۰۰۰۰۱");
            reloaded.SubmittedAtUtc.Should().NotBeNull();
            reloaded.SubmittedAtUtc!.Should().BeCloseTo(beforeSubmit, TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CreateSale_SubmitThenApprove_StatusApprovedAndApprovedBySet()
    {
        // Arrange
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.AddLineItem(TestValues.ProductId, "Apple", 1, new Money(1.00m, TestValues.USD));
            sale.Submit(SaleNumber.Create(1403, 1));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            var beforeApprove = DateTime.UtcNow;

            // Act — approve the sale.
            sale = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            sale!.Approve(TestValues.ApprovedByUserId);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert
            db.ChangeTracker.Clear();
            var reloaded = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            reloaded!.Status.Should().Be(SaleStatus.Approved);
            reloaded.ApprovedByUserId.Should().Be(TestValues.ApprovedByUserId);
            reloaded.ApprovedAtUtc.Should().NotBeNull();
            reloaded.ApprovedAtUtc!.Should().BeCloseTo(beforeApprove, TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CreateSale_ApproveThenInvoice_StatusInvoicedAndInvoicedBySet()
    {
        // Arrange — Submit + Approve first.
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.AddLineItem(TestValues.ProductId, "Apple", 1, new Money(1.00m, TestValues.USD));
            sale.Submit(SaleNumber.Create(1403, 1));
            sale.Approve(TestValues.ApprovedByUserId);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            var beforeInvoice = DateTime.UtcNow;

            // Act
            sale = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            sale!.MarkAsInvoiced(TestValues.InvoicedByUserId);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert
            db.ChangeTracker.Clear();
            var reloaded = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            reloaded!.Status.Should().Be(SaleStatus.Invoiced);
            reloaded.InvoicedByUserId.Should().Be(TestValues.InvoicedByUserId);
            reloaded.InvoicedAtUtc.Should().NotBeNull();
            reloaded.InvoicedAtUtc!.Should().BeCloseTo(beforeInvoice, TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task CreateSale_SubmitThenCancel_StatusCancelledAndReasonSet()
    {
        // Arrange — Submit first.
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.AddLineItem(TestValues.ProductId, "Apple", 1, new Money(1.00m, TestValues.USD));
            sale.Submit(SaleNumber.Create(1403, 1));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            var beforeCancel = DateTime.UtcNow;

            // Act
            sale = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            sale!.Cancel(TestValues.CancelledByUserId, "out of stock");
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // Assert
            db.ChangeTracker.Clear();
            var reloaded = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            reloaded!.Status.Should().Be(SaleStatus.Cancelled);
            reloaded.CancelledByUserId.Should().Be(TestValues.CancelledByUserId);
            reloaded.CancelledAtUtc.Should().NotBeNull();
            reloaded.CancelledAtUtc!.Should().BeCloseTo(beforeCancel, TimeSpan.FromSeconds(5));
            reloaded.CancellationReason.Should().Be("out of stock");
        }
    }

    // Verifies the state machine rejects a second Submit on an already-
    // submitted sale. The domain throws DomainException ("Cannot modify
    // line items of a sale that is not in Draft. Current status:
    // 'Pending'.") — the integration test catches it directly (the
    // handler-level test verifies the handler converts it to Result.Failure).
    [Fact]
    public async Task CreateSale_SubmitTwice_ThrowsDomainException()
    {
        // Arrange — Submit first.
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            sale = await saleRepo.GetByIdWithLineItemsAsync(sale.Id, CancellationToken.None);
            sale!.AddLineItem(TestValues.ProductId, "Apple", 1, new Money(1.00m, TestValues.USD));
            sale.Submit(SaleNumber.Create(1403, 1));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            // Act — second Submit on a Pending sale must throw.
            sale = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            var act = () => sale!.Submit(SaleNumber.Create(1403, 2));

            // Assert — DomainException with the "not in Draft" message.
            act.Should().Throw<DomainException>()
                .WithMessage("*not in Draft*Pending*");
        }
    }

    // Verifies the state machine rejects Cancel on a Draft sale. Drafts are
    // hard-deleted via the repository, not cancelled — the domain's
    // EnsureCancellable() guard throws to enforce this.
    [Fact]
    public async Task CreateSale_CancelBeforeSubmit_ThrowsDomainException()
    {
        // Arrange
        var (saleRepo, unitOfWork, db) = await BuildWiredCollaboratorsAsync();
        await using (db)
        {
            var sale = Sale.Create(
                TestValues.CustomerId, CustomerName,
                TestValues.CreatedByUserId, CreatedByName);
            await saleRepo.AddAsync(sale, CancellationToken.None);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            // Act — Cancel on a Draft sale must throw.
            sale = await saleRepo.GetByIdAsync(sale.Id, CancellationToken.None);
            var act = () => sale!.Cancel(TestValues.CancelledByUserId, "test");

            // Assert — DomainException explaining drafts can't be cancelled.
            act.Should().Throw<DomainException>()
                .WithMessage("*Cannot cancel a Draft sale*");
        }
    }
}

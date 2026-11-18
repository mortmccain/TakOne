using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.Commands.ApproveSale;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.Enums;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Sales.Commands.ApproveSale;

/// <summary>
/// Unit tests for <see cref="ApproveSaleCommandHandler"/> — the FIRST
/// tests this handler ever had, written for the owner's
/// separation-of-duties rule:
///
///   "no one (if they have access to confirming) can confirm their own
///    sale / order."
///
/// The handler now rejects any approval attempt where
/// <c>sale.CustomerId == currentUser.UserId</c> — the approver is the
/// buyer of record — with the culture-neutral
/// <see cref="SelfApprovalErrors"/> code. A staff member approving a
/// sale they created FOR SOMEONE ELSE (on-behalf draft) is NOT blocked:
/// they are not the buyer.
///
/// COVERAGE APPROACH: the Sale and Product aggregates are REAL (built
/// via <c>Sale.Create</c> + <c>AddLineItem</c> + <c>Submit</c> and
/// <c>Product.Create</c>), so the Pending → Approved transition and the
/// stock decrement run exactly like production. All collaborators are
/// NSubstitute mocks; the unit-of-work fake passes the retry lambda
/// straight through (single attempt).
/// </summary>
public class ApproveSaleCommandHandlerTests
{
    // ── Test doubles ────────────────────────────────────────────────────

    /// <summary>
    /// Unit-of-work double that runs the retry lambda immediately (one
    /// attempt — no DB conflicts in unit tests) and counts SaveChanges.
    /// </summary>
    private sealed class PassthroughUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }

        public void ClearChangeTracker() { /* no-op for tests */ }

        public void AddEntity(object entity) { /* no-op for tests */ }

        public Task<T> ExecuteWithRetryAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            int maxAttempts = 3,
            CancellationToken cancellationToken = default)
            => operation(cancellationToken);
    }

    /// <summary>A lock handle double — acquiring costs nothing.</summary>
    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── Fixture builders ────────────────────────────────────────────────

    private static Product BuildInStockProduct()
        => Product.Create(
            name: "Test Product",
            description: "A test product",
            price: new Money(10m, TestValues.IRR),
            stockQuantity: 100,
            categoryId: TestValues.CategoryId);

    /// <summary>
    /// Builds a REAL Pending sale for the given customer with one line
    /// of the given product — the exact aggregate state ApproveSale
    /// expects to load.
    /// </summary>
    private static Sale BuildPendingSale(User customer, User creator, Product product)
    {
        var sale = Sale.Create(
            customerId: customer.Id,
            customerName: customer.FullName,
            createdByUserId: creator.Id,
            createdByName: creator.FullName);

        sale.AddLineItem(
            productId: product.Id,
            productName: product.Name,
            quantity: 1,
            unitPrice: product.Price);

        sale.Submit(SaleNumber.Create(TestValues.PersianYearValid, 1));
        return sale;
    }

    /// <summary>
    /// Wires the full mock set. The approver is an Employee by default
    /// (the minimal role that passes the handler's defense-in-depth
    /// role check); tests override the role configuration when they
    /// need to.
    /// </summary>
    private static (
        ICurrentUserService currentUser,
        ISaleRepository saleRepo,
        IProductRepository productRepo,
        ISaleStateLock saleStateLock,
        PassthroughUnitOfWork unitOfWork,
        ILogger<ApproveSaleCommandHandler> logger)
        BuildMocks(Sale sale, Product product)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.ApprovedByUserId);
        currentUser.FullName.Returns("Approving Employee");
        currentUser.IsInRole(Roles.Employee).Returns(true);
        currentUser.IsInRole(Roles.Manager).Returns(false);
        currentUser.IsInRole(Roles.Admin).Returns(false);

        var saleRepo = Substitute.For<ISaleRepository>();
        saleRepo.GetByIdWithLineItemsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(sale);

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdsAsync(
                Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product> { product });

        var saleStateLock = Substitute.For<ISaleStateLock>();
        saleStateLock.AcquireAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new NoopAsyncDisposable());

        var unitOfWork = new PassthroughUnitOfWork();

        var logger = Substitute.For<ILogger<ApproveSaleCommandHandler>>();

        return (currentUser, saleRepo, productRepo, saleStateLock, unitOfWork, logger);
    }

    // ── Tests: the self-approval guard ──────────────────────────────────

    // The owner's separation-of-duties rule: an Employee (any role with
    // confirmation rights) tries to approve a sale in which THEY are the
    // customer → rejected with the SelfApproval stable code, BEFORE any
    // state transition or stock decrement.
    [Fact]
    public async Task HandleAsync_WhenApproverIsTheCustomer_ReturnsSelfApprovalFailure()
    {
        // Arrange — the approver IS the customer on the sale (a
        // self-serve shop buy: buyer is both customer and creator).
        var buyer = User.CreateStaff("EMP-100", "Buying Employee");
        var product = BuildInStockProduct();
        var sale = BuildPendingSale(buyer, buyer, product);

        var (currentUser, saleRepo, productRepo, saleStateLock, unitOfWork, logger)
            = BuildMocks(sale, product);
        currentUser.UserId.Returns(buyer.Id);

        // Act
        var result = await ApproveSaleCommandHandler.HandleAsync(
            new ApproveSaleCommand(sale.Id),
            currentUser, saleRepo, productRepo, saleStateLock,
            unitOfWork, logger, CancellationToken.None);

        // Assert — rejected with the culture-neutral stable code the UI
        // localizes into "you cannot confirm your own order".
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SelfApprovalErrors.Format(),
            "the approver is the buyer of record (sale.CustomerId == currentUser.UserId)");

        // The sale must be UNTOUCHED — still Pending, no stock decrement,
        // no SaveChanges.
        sale.Status.Should().Be(SaleStatus.Pending,
            "the self-approval guard fires before the state transition");
        product.StockQuantity.Should().Be(100,
            "stock is only decremented after a legitimate approval");
        unitOfWork.SaveCount.Should().Be(0,
            "a rejected approval must not persist anything");
    }

    // The guard must hold for Admins and Managers too — "no one" means
    // no one. Same rejection, different role.
    [Theory]
    [InlineData(Roles.Admin)]
    [InlineData(Roles.Manager)]
    [InlineData(Roles.Employee)]
    public async Task HandleAsync_WhenApproverOfAnyRoleIsTheCustomer_ReturnsSelfApprovalFailure(string role)
    {
        // Arrange — a high-role staff member who is also the buyer.
        var buyer = User.CreateStaff("STAFF-100", "Buying Staff Member");
        var product = BuildInStockProduct();
        var sale = BuildPendingSale(buyer, buyer, product);

        var (currentUser, saleRepo, productRepo, saleStateLock, unitOfWork, logger)
            = BuildMocks(sale, product);
        currentUser.UserId.Returns(buyer.Id);
        currentUser.IsInRole(Roles.Employee).Returns(role == Roles.Employee);
        currentUser.IsInRole(Roles.Manager).Returns(role == Roles.Manager);
        currentUser.IsInRole(Roles.Admin).Returns(role == Roles.Admin);

        // Act
        var result = await ApproveSaleCommandHandler.HandleAsync(
            new ApproveSaleCommand(sale.Id),
            currentUser, saleRepo, productRepo, saleStateLock,
            unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(SelfApprovalErrors.Format(),
            "no role — not even Admin — permits confirming one's own order");
        unitOfWork.SaveCount.Should().Be(0);
    }

    // ── Tests: the legitimate paths the guard must NOT break ───────────

    // A staff member approving a sale they created FOR someone else is
    // NOT self-approval — they are not the buyer. This is the normal
    // on-behalf flow and must keep working.
    [Fact]
    public async Task HandleAsync_WhenApproverCreatedSaleForAnotherBuyer_Succeeds()
    {
        // Arrange — staff created the draft on the customer's behalf:
        // creator = staff, customer = someone else.
        var customer = User.CreateCustomer("CUST-001", "Test Customer", TestValues.GroupId);
        var staffCreator = User.CreateStaff("EMP-001", "Creating Employee");
        var product = BuildInStockProduct();
        var sale = BuildPendingSale(customer, staffCreator, product);

        var (currentUser, saleRepo, productRepo, saleStateLock, unitOfWork, logger)
            = BuildMocks(sale, product);
        currentUser.UserId.Returns(staffCreator.Id);

        // Act
        var result = await ApproveSaleCommandHandler.HandleAsync(
            new ApproveSaleCommand(sale.Id),
            currentUser, saleRepo, productRepo, saleStateLock,
            unitOfWork, logger, CancellationToken.None);

        // Assert — approved normally: state moved, stock decremented,
        // audit fields set.
        result.IsSuccess.Should().BeTrue();
        sale.Status.Should().Be(SaleStatus.Approved);
        sale.ApprovedByUserId.Should().Be(staffCreator.Id);
        product.StockQuantity.Should().Be(99,
            "one unit of stock is decremented for the single line");
        unitOfWork.SaveCount.Should().Be(1);
    }

    // The plain third-party approval (approver is neither the customer
    // nor the creator) — the most common path — must keep working.
    [Fact]
    public async Task HandleAsync_WhenApproverIsNeitherBuyerNorCreator_Succeeds()
    {
        // Arrange
        var customer = User.CreateCustomer("CUST-002", "Another Customer", TestValues.GroupId);
        var creator = User.CreateStaff("EMP-002", "Other Employee");
        var product = BuildInStockProduct();
        var sale = BuildPendingSale(customer, creator, product);

        var (currentUser, saleRepo, productRepo, saleStateLock, unitOfWork, logger)
            = BuildMocks(sale, product);
        // currentUser.UserId defaults to TestValues.ApprovedByUserId —
        // a third party, distinct from customer and creator.

        // Act
        var result = await ApproveSaleCommandHandler.HandleAsync(
            new ApproveSaleCommand(sale.Id),
            currentUser, saleRepo, productRepo, saleStateLock,
            unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        sale.Status.Should().Be(SaleStatus.Approved);
        product.StockQuantity.Should().Be(99);
    }
}

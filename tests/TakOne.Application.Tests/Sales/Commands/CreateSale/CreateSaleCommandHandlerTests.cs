using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.Commands.CreateSale;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.Application.Tests.Sales.Commands.CreateSale;

/// <summary>
/// Regression tests for <see cref="CreateSaleCommandHandler"/> — focusing
/// on the impersonation-prevention fix from Brutal Code Review v3
/// finding #04.
/// </summary>
/// <remarks>
/// <para>
/// <b>BUG HISTORY (Brutal Code Review v3 finding #04):</b> The previous
/// handler did NOT verify that the resolved user (the one whose WorkerId
/// was passed in the command) actually held the Customer role, NOR that
/// when the CALLER was a Customer, they were creating a sale for
/// THEMSELVES. Any authenticated user could pass another user's WorkerId
/// (any role!) and create a sale with that user as CustomerId — bypassing
/// their own purchase limits, salary budget, and currency restrictions.
/// An Employee could even create a sale with an Admin as CustomerId.
/// </para>
/// <para>
/// <b>THE FIX (Round 18 production code, not by this subagent):</b>
/// The handler now calls <c>userRepository.GetRolesByUserIdsAsync</c>
/// on the resolved customer's Id. If the returned roles do NOT include
/// <c>Roles.Customer</c>, the sale is rejected with "is not a customer".
/// If the CALLER is a Customer (non-staff), the handler additionally
/// verifies <c>customer.Id == currentUser.UserId</c> — otherwise it
/// rejects with "Customers can only create sales for themselves."
/// </para>
/// <para>
/// <b>THESSE TESTS</b> cover the 6 paths the impersonation fix added,
/// plus the existing pre-fix paths (not-found, inactive). The Sale
/// aggregate is REAL (not mocked) — we use <see cref="Sale.Create"/> +
/// <see cref="Sale.AddLineItem"/> for the success path. All collaborators
/// are NSubstitute mocks. <see cref="TestValues"/> supplies the stable
/// Guids (GroupId, ProductId, etc.) so failure diffs are readable.
/// </para>
/// <para>
/// <b>WHY THE CALLER'S USERID IS AUTO-GENERATED:</b> The User aggregate's
/// factory methods (<c>CreateCustomer</c> / <c>CreateStaff</c>) call
/// <c>AggregateRoot()</c> ctor which assigns <c>Guid.NewGuid()</c> — there
/// is no public API to set a specific Id. Tests that need
/// <c>currentUser.UserId == customer.Id</c> (the self-buy case) simply
/// read <c>customer.Id</c> AFTER constructing the User and configure the
/// <c>currentUser.UserId</c> mock to return that value. The randomness
/// of the Guid doesn't affect the test outcome — what matters is the
/// EQUALITY, which we make explicit.
/// </para>
/// </remarks>
public class CreateSaleCommandHandlerTests
{
    // ── Constants ────────────────────────────────────────────────────

    private const string CustomerWorkerId = "CUST-001";
    private const string AnotherCustomerWorkerId = "CUST-002";
    private const string AdminWorkerId = "ADMIN-001";
    private const string EmployeeWorkerId = "EMP-001";
    private const string MissingWorkerId = "MISSING-001";

    // ── Helpers ──────────────────────────────────────────────────────

    // Builds a fully-wired NSubstitute mock set for the handler:
    //   - currentUser authenticated (UserId/FullName configured per-test)
    //   - userRepository.GetByWorkerIdAsync returns null by default (each
    //     test overrides for the path it exercises)
    //   - userRepository.GetRolesByUserIdsAsync returns an empty dict by
    //     default (the not-a-customer path)
    //   - productRepository.GetByIdAsync returns a real Product with
    //     stock 100, price 10 IRR (each test overrides if it needs a
    //     different product)
    //   - purchaseLimitPolicy.GetCountLimitAsync returns null (no limit
    //     enforced — the success path needs no limit)
    //   - saleRepository.AddAsync is a no-op
    //   - unitOfWork.SaveChangesAsync returns 1
    private static (
        ICurrentUserService currentUser,
        IUserRepository userRepo,
        IProductRepository productRepo,
        ISaleRepository saleRepo,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        IUnitOfWork unitOfWork,
        ILogger<CreateSaleCommandHandler> logger)
        BuildMocks()
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);
        currentUser.FullName.Returns("Test Caller");

        var userRepo = Substitute.For<IUserRepository>();
        // Default: no user resolves — every test that needs a resolved
        // customer overrides this. The "not found" test relies on the
        // default returning null (which NSubstitute does for reference
        // types by default).
        userRepo.GetByWorkerIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);
        // Default: empty roles map — the "no roles" test relies on this.
        userRepo.GetRolesByUserIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>>());

        var productRepo = Substitute.For<IProductRepository>();
        // Default: a real Product with stock 100, price 10 IRR — used by
        // the success path tests. Tests that fail before reaching the
        // product lookup (auth/customer-not-found/inactive/not-customer/
        // impersonation) don't touch this mock.
        productRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(BuildInStockProduct());

        var saleRepo = Substitute.For<ISaleRepository>();
        // AddAsync returns Task — no setup needed, NSubstitute returns a
        // completed Task by default for void-returning async methods.

        var purchaseLimitPolicy = Substitute.For<IPurchaseLimitPolicy>();
        purchaseLimitPolicy.GetCountLimitAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns((int?)null); // No limit — the success path needs no limit enforcement.

        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(1);

        var logger = Substitute.For<ILogger<CreateSaleCommandHandler>>();

        return (currentUser, userRepo, productRepo, saleRepo, purchaseLimitPolicy, unitOfWork, logger);
    }

    // Builds a real Product via Product.Create — used by the success
    // path tests where the handler iterates the line items. Stock is
    // deliberately high (100) so any reasonable quantity passes the
    // stock check; price is a valid Money (10 IRR).
    private static Product BuildInStockProduct()
    {
        return Product.Create(
            name: "Test Product",
            description: "A test product",
            price: new Money(10m, TestValues.IRR),
            stockQuantity: 100,
            categoryId: TestValues.CategoryId);
    }

    // Builds a valid CreateSaleCommand — single line item of quantity 1
    // for the supplied product Id. The customer's WorkerId is the
    // parameter that varies between tests.
    private static CreateSaleCommand BuildCommand(string customerWorkerId)
    {
        return new CreateSaleCommand(
            CustomerWorkerId: customerWorkerId,
            Items: new[]
            {
                new CreateSaleItem(TestValues.ProductId, 1),
            });
    }

    // ── Test 1: Self-buy success ────────────────────────────────────

    // The Customer passes their OWN WorkerId → the repository resolves
    // them → the resolved user is themselves (customer.Id == currentUser.UserId)
    // → the resolved user holds the Customer role (via GetRolesByUserIdsAsync)
    // → caller IS a Customer AND customer.Id == currentUser.UserId →
    // no impersonation rejection → success.
    [Fact]
    public async Task HandleAsync_WhenCustomerCreatesOwnSale_ReturnsSuccess()
    {
        // Arrange — a real Customer user. The factory method assigns a
        // random Id; we configure currentUser.UserId to return THAT Id
        // so the self-buy identity match holds.
        var customer = User.CreateCustomer(CustomerWorkerId, "Test Customer", TestValues.GroupId);
        var (currentUser, userRepo, productRepo, saleRepo, purchaseLimitPolicy, unitOfWork, logger) = BuildMocks();
        currentUser.UserId.Returns(customer.Id);
        currentUser.IsInRole(Roles.Customer).Returns(true);
        currentUser.IsInRole(Roles.Employee).Returns(false);

        userRepo.GetByWorkerIdAsync(CustomerWorkerId, Arg.Any<CancellationToken>())
            .Returns(customer);
        userRepo.GetRolesByUserIdsAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(customer.Id)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>> { [customer.Id] = new() { Roles.Customer } });

        var command = BuildCommand(CustomerWorkerId);

        // Act
        var result = await CreateSaleCommandHandler.HandleAsync(
            command, currentUser, userRepo, productRepo, saleRepo,
            purchaseLimitPolicy, unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty,
            "the returned Guid is the new Sale's Id — must be a fresh non-empty Guid");
        await saleRepo.Received(1).AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Test 2: Impersonation rejection ──────────────────────────────

    // The CALLER is a Customer; they pass ANOTHER customer's WorkerId.
    // The handler resolves the target customer, confirms the target
    // holds the Customer role, but then checks
    // `callerIsCustomer && customer.Id != currentUser.UserId` — true →
    // return Failure with "Customers can only create sales for themselves."
    //
    // This is the regression for the impersonation hole: previously the
    // handler didn't check that caller and target matched when the caller
    // was a Customer. The fix closes the hole.
    [Fact]
    public async Task HandleAsync_WhenCustomerCreatesSaleForAnotherCustomer_ReturnsImpersonationRejected()
    {
        // Arrange — two distinct customers. The caller's UserId is the
        // caller's Id; the target customer (resolved from WorkerId) is
        // a DIFFERENT user with a different Id.
        var callerCustomer = User.CreateCustomer(EmployeeWorkerId, "Caller Customer", TestValues.GroupId);
        var targetCustomer = User.CreateCustomer(AnotherCustomerWorkerId, "Target Customer", TestValues.GroupId);

        var (currentUser, userRepo, productRepo, saleRepo, purchaseLimitPolicy, unitOfWork, logger) = BuildMocks();
        currentUser.UserId.Returns(callerCustomer.Id);
        currentUser.IsInRole(Roles.Customer).Returns(true);
        currentUser.IsInRole(Roles.Employee).Returns(false);

        userRepo.GetByWorkerIdAsync(AnotherCustomerWorkerId, Arg.Any<CancellationToken>())
            .Returns(targetCustomer);
        userRepo.GetRolesByUserIdsAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(targetCustomer.Id)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>> { [targetCustomer.Id] = new() { Roles.Customer } });

        var command = BuildCommand(AnotherCustomerWorkerId);

        // Act
        var result = await CreateSaleCommandHandler.HandleAsync(
            command, currentUser, userRepo, productRepo, saleRepo,
            purchaseLimitPolicy, unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Customers can only create sales for themselves",
            "the caller is a Customer and the resolved customer.Id differs from currentUser.UserId — the impersonation check trips");
        // The handler must NOT have persisted the sale — the impersonation
        // rejection happens BEFORE the persistence step.
        await saleRepo.DidNotReceive().AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Test 3: Employee buys on behalf of a customer ───────────────

    // The CALLER is an Employee (not a Customer). They pass a Customer's
    // WorkerId → the resolved customer holds the Customer role → the
    // caller is NOT a Customer → no impersonation check → success.
    //
    // This is the legitimate "staff buying on behalf" flow — the original
    // business case for having CustomerWorkerId on the command at all.
    [Fact]
    public async Task HandleAsync_WhenEmployeeCreatesSaleForCustomer_ReturnsSuccess()
    {
        // Arrange — Employee caller (Group-less staff) + Customer target.
        var employeeCaller = User.CreateStaff(EmployeeWorkerId, "Test Employee");
        var customer = User.CreateCustomer(CustomerWorkerId, "Test Customer", TestValues.GroupId);

        var (currentUser, userRepo, productRepo, saleRepo, purchaseLimitPolicy, unitOfWork, logger) = BuildMocks();
        currentUser.UserId.Returns(employeeCaller.Id);
        currentUser.IsInRole(Roles.Customer).Returns(false);
        currentUser.IsInRole(Roles.Employee).Returns(true);

        userRepo.GetByWorkerIdAsync(CustomerWorkerId, Arg.Any<CancellationToken>())
            .Returns(customer);
        userRepo.GetRolesByUserIdsAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(customer.Id)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>> { [customer.Id] = new() { Roles.Customer } });

        var command = BuildCommand(CustomerWorkerId);

        // Act
        var result = await CreateSaleCommandHandler.HandleAsync(
            command, currentUser, userRepo, productRepo, saleRepo,
            purchaseLimitPolicy, unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);
        // Staff buying on behalf DOES persist — the legitimate flow.
        await saleRepo.Received(1).AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Test 4: Employee targets a non-customer ─────────────────────

    // The CALLER is an Employee. They pass an Admin's WorkerId → the
    // resolved user holds the Admin role, NOT the Customer role → the
    // `customerRoles.Contains(Roles.Customer)` check fails → return
    // Failure with "is not a customer".
    //
    // This is the regression for the "could create a sale with an Admin
    // as CustomerId" hole. Previously the handler only checked IsActive
    // — the Admin user IS active, so the sale would go through with the
    // Admin as the customer (bypassing the Admin's group's salary budget,
    // etc.). The fix rejects this.
    [Fact]
    public async Task HandleAsync_WhenEmployeeCreatesSaleForNonCustomer_ReturnsNotACustomer()
    {
        // Arrange — Employee caller + Admin target.
        var employeeCaller = User.CreateStaff(EmployeeWorkerId, "Test Employee");
        var adminTarget = User.CreateStaff(AdminWorkerId, "Test Admin");

        var (currentUser, userRepo, productRepo, saleRepo, purchaseLimitPolicy, unitOfWork, logger) = BuildMocks();
        currentUser.UserId.Returns(employeeCaller.Id);
        currentUser.IsInRole(Roles.Customer).Returns(false);
        currentUser.IsInRole(Roles.Employee).Returns(true);

        userRepo.GetByWorkerIdAsync(AdminWorkerId, Arg.Any<CancellationToken>())
            .Returns(adminTarget);
        // The resolved Admin user's roles are [Roles.Admin] — NOT Customer.
        userRepo.GetRolesByUserIdsAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(adminTarget.Id)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>> { [adminTarget.Id] = new() { Roles.Admin } });

        var command = BuildCommand(AdminWorkerId);

        // Act
        var result = await CreateSaleCommandHandler.HandleAsync(
            command, currentUser, userRepo, productRepo, saleRepo,
            purchaseLimitPolicy, unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("is not a customer",
            "the resolved user has only the Admin role — the Customer role is absent, so the is-customer check fails");
        await saleRepo.DidNotReceive().AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
    }

    // ── Test 5: Resolved user not found ──────────────────────────────

    // The WorkerId doesn't resolve to any user → the repository returns
    // null → the handler returns Failure with "No user found with worker ID".
    //
    // This is the pre-fix rejection path — included for completeness
    // so the test suite covers the full handler decision tree.
    [Fact]
    public async Task HandleAsync_WhenCustomerNotFound_ReturnsUserNotFound()
    {
        // Arrange — the default mock returns null for GetByWorkerIdAsync.
        var (currentUser, userRepo, productRepo, saleRepo, purchaseLimitPolicy, unitOfWork, logger) = BuildMocks();
        currentUser.IsInRole(Roles.Customer).Returns(false);

        var command = BuildCommand(MissingWorkerId);

        // Act
        var result = await CreateSaleCommandHandler.HandleAsync(
            command, currentUser, userRepo, productRepo, saleRepo,
            purchaseLimitPolicy, unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No user found with worker ID",
            "the WorkerId doesn't resolve to any user — the handler returns the not-found error");
        result.Error.Should().Contain(MissingWorkerId,
            "the error message includes the unresolved WorkerId for diagnostics");
        await saleRepo.DidNotReceive().AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
    }

    // ── Test 6: Resolved customer is inactive ───────────────────────

    // The WorkerId resolves to a user, but that user has been deactivated
    // (IsActive=false via the Deactivate() domain method). The handler
    // returns Failure with "is inactive". This rejection happens BEFORE
    // the role-lookup step — so GetRolesByUserIdsAsync is NOT called.
    [Fact]
    public async Task HandleAsync_WhenCustomerInactive_ReturnsInactive()
    {
        // Arrange — construct an active customer, then deactivate it.
        var customer = User.CreateCustomer(CustomerWorkerId, "Test Customer", TestValues.GroupId);
        customer.Deactivate(); // IsActive = false

        var (currentUser, userRepo, productRepo, saleRepo, purchaseLimitPolicy, unitOfWork, logger) = BuildMocks();
        currentUser.IsInRole(Roles.Customer).Returns(false);

        userRepo.GetByWorkerIdAsync(CustomerWorkerId, Arg.Any<CancellationToken>())
            .Returns(customer);

        var command = BuildCommand(CustomerWorkerId);

        // Act
        var result = await CreateSaleCommandHandler.HandleAsync(
            command, currentUser, userRepo, productRepo, saleRepo,
            purchaseLimitPolicy, unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("inactive",
            "the resolved user's IsActive flag is false — the handler rejects the sale before any role check");
        result.Error.Should().Contain(CustomerWorkerId);
        // The role-lookup must NOT have been called — the inactive check
        // returns early.
        await userRepo.DidNotReceive().GetRolesByUserIdsAsync(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
        await saleRepo.DidNotReceive().AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
    }

    // ── Test 7: Resolved user has no roles ──────────────────────────

    // The WorkerId resolves to a user, the user IS active, but
    // GetRolesByUserIdsAsync returns an empty dictionary for that user
    // (e.g. role seeding was incomplete). The handler treats a missing
    // roles key as "no roles" → "is not a customer" → Failure.
    [Fact]
    public async Task HandleAsync_WhenResolvedUserHasNoRoles_ReturnsNotACustomer()
    {
        // Arrange — a real active Customer user, but the role map returns
        // an empty dict (no entry for the customer's Id).
        var customer = User.CreateCustomer(CustomerWorkerId, "Test Customer", TestValues.GroupId);

        var (currentUser, userRepo, productRepo, saleRepo, purchaseLimitPolicy, unitOfWork, logger) = BuildMocks();
        currentUser.UserId.Returns(customer.Id);
        currentUser.IsInRole(Roles.Customer).Returns(true); // caller IS a Customer

        userRepo.GetByWorkerIdAsync(CustomerWorkerId, Arg.Any<CancellationToken>())
            .Returns(customer);
        // Empty dict — the customer's Id is NOT a key. The handler treats
        // this as "no roles" → not a Customer → rejection.
        userRepo.GetRolesByUserIdsAsync(Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(customer.Id)), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, List<string>>());

        var command = BuildCommand(CustomerWorkerId);

        // Act
        var result = await CreateSaleCommandHandler.HandleAsync(
            command, currentUser, userRepo, productRepo, saleRepo,
            purchaseLimitPolicy, unitOfWork, logger, CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("is not a customer",
            "the resolved user has no roles (rare — incomplete role seeding) — the is-customer check fails");
        await saleRepo.DidNotReceive().AddAsync(Arg.Any<Sale>(), Arg.Any<CancellationToken>());
    }
}

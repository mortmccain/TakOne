using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Products.DTOs;
using TakOne.Application.Products.Queries.GetProductById;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using TakOne.Testing.Builders;
using Xunit;

namespace TakOne.Application.Tests.Products.Queries.GetProductById;

/// <summary>
/// Unit tests for <see cref="GetProductByIdQueryHandler"/>.
///
/// COVERAGE APPROACH: the handler is a static method that takes the
/// query, the current-user service, the product repository, the user
/// repository, the purchase-limit policy, a logger, and a cancellation
/// token. We mock every collaborator with NSubstitute. The product
/// repository returns a REAL <see cref="Domain.Products.Entities.Product"/>
/// instance (built via <see cref="ProductBuilder"/>) so we can observe
/// the projection shape on actual property values.
///
/// COVERAGE TARGETS:
///   1. Auth rejection (not authenticated, UserId empty) — Failure
///      "Authentication required.".
///   2. Product not found — Failure $"Product '{productId}' was not found.".
///   3. canSeeAllLimits = true for Admin, Manager, Employee — purchase
///      limits projected into the DTO.
///   4. canSeeAllLimits = false for Customer, ReadOnly — purchase
///      limits collection is empty.
///   5. freshUser.GroupId resolved from the DB (NOT from the stale claim).
///   6. MyPurchaseLimit via purchaseLimitPolicy.GetCountLimitAsync —
///      honors LimitMode (returns null when SalaryOnly).
///   7. MoneyDto population (Amount + Currency).
///   8. Logger calls (Warning on auth-fail + not-found, Information on
///      not-found only — there's NO success-path Information log).
///   9. CancellationToken forwarded to all DB calls.
///  10. GroupId null (staff user) → MyPurchaseLimit null.
///  11. PurchaseLimits population only when canSeeAllLimits (and the
///      limits from the product are correctly projected).
///  12. canSeeAllLimits false + ProductPurchaseLimitDto collection empty.
///  13. Product's category fields (CategoryId, SubCategoryId,
///      SubSubCategoryId) projected verbatim.
///  14. Product's Name/Description/PictureUrl/StockQuantity projected.
/// </summary>
public class GetProductByIdQueryHandlerTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static GetProductByIdQuery BuildValidQuery(Guid? productId = null)
        => new() { ProductId = productId ?? TestValues.ProductId };

    // Builds a fully-wired NSubstitute environment with the current user
    // as an Admin (canSeeAllLimits=true), a real Product, and a fresh
    // User loaded from the DB (GroupId=null for staff).
    private static (
        ICurrentUserService currentUser,
        IProductRepository productRepo,
        IUserRepository userRepo,
        IPurchaseLimitPolicy purchaseLimitPolicy,
        ILogger<GetProductByIdQueryHandler> logger,
        Domain.Products.Entities.Product product)
        BuildMocks(
        Domain.Products.Entities.Product? product = null,
        User? freshUser = null,
        string? role = Roles.Admin)
    {
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(TestValues.CreatedByUserId);
        // Default: Admin role (canSeeAllLimits=true).
        currentUser.IsInRole(Arg.Any<string>())
            .Returns(role == Roles.Admin || role == Roles.Manager || role == Roles.Employee);

        // Use a REAL Product so we can observe the projection shape on
        // actual property values. Start at stock=10 with USD price.
        var actualProduct = product ?? new ProductBuilder()
            .WithName("Test Product")
            .WithStock(10)
            .WithPrice(new Money(100m, TestValues.USD))
            .Build();

        var productRepo = Substitute.For<IProductRepository>();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualProduct);

        // Build a fresh User from the DB — staff users have GroupId=null.
        // The handler uses this to resolve the caller's CURRENT group
        // (the GroupId claim on the auth cookie is a snapshot from
        // login time and goes stale when an admin reassigns the user's
        // group after they're already logged in).
        var actualFreshUser = freshUser ?? User.CreateStaff("EMP-001", "Test Admin");

        var userRepo = Substitute.For<IUserRepository>();
        userRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs(actualFreshUser);

        var purchaseLimitPolicy = Substitute.For<IPurchaseLimitPolicy>();
        // Default: no count limit (returns null — typical for staff with
        // GroupId=null).
        purchaseLimitPolicy.GetCountLimitAsync(default, default, default)
            .ReturnsForAnyArgs((int?)null);

        var logger = Substitute.For<ILogger<GetProductByIdQueryHandler>>();

        return (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, actualProduct);
    }

    // ── Happy path ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_ReturnsSuccessWithProductDto()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
    }

    // ── Auth rejection ─────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
        // Auth rejection short-circuits BEFORE any repo / policy call.
        await productRepo.DidNotReceive().GetByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenUserIdIsEmpty_ReturnsAuthenticationRequired()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.Empty);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Authentication required.");
    }

    // ── Not found ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenProductNotFound_ReturnsFailureWithProductId()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Domain.Products.Entities.Product?)null);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Product '{TestValues.ProductId}' was not found.");
        // Not-found short-circuits BEFORE the user repo + policy calls.
        await userRepo.DidNotReceive().GetByIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await purchaseLimitPolicy.DidNotReceive().GetCountLimitAsync(
            Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    // ── canSeeAllLimits logic ───────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCallerIsAdmin_IncludesPurchaseLimitsInDto()
    {
        // Arrange
        // Build a product with one PurchaseLimit. ProductBuilder doesn't
        // expose a WithPurchaseLimit method, so we use the Product's
        // public SetPurchaseLimit method on the built instance.
        var product = new ProductBuilder().Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);

        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks(product, role: Roles.Admin);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.PurchaseLimits.Should().NotBeEmpty();
        result.Value.PurchaseLimits.Should().Contain(pl => pl.GroupId == TestValues.GroupId && pl.Limit == 5);
    }

    [Fact]
    public async Task HandleAsync_WhenCallerIsManager_IncludesPurchaseLimitsInDto()
    {
        // Arrange
        // Manager is also a "staff" role — canSeeAllLimits=true.
        var product = new ProductBuilder().Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks(product, role: Roles.Manager);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Value.PurchaseLimits.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenCallerIsEmployee_IncludesPurchaseLimitsInDto()
    {
        // Arrange
        // Employee is also a "staff" role — canSeeAllLimits=true.
        var product = new ProductBuilder().Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks(product, role: Roles.Employee);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Value.PurchaseLimits.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenCallerIsCustomer_StripsPurchaseLimitsFromDto()
    {
        // Arrange
        // Customer is NOT a "staff" role — canSeeAllLimits=false. The
        // PurchaseLimits collection is cleared in the DTO (defense-in-
        // depth: the customer should only see THEIR OWN limit, never
        // other groups' limits).
        var product = new ProductBuilder().Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);
        product.SetPurchaseLimit(TestValues.GroupId2, 10);
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks(product, role: Roles.Customer);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Value.PurchaseLimits.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_WhenCallerIsReadOnly_StripsPurchaseLimitsFromDto()
    {
        // Arrange
        // ReadOnly is intentionally excluded — they can view but not edit,
        // and limits are an internal staff detail that doesn't help them.
        var product = new ProductBuilder().Build();
        product.SetPurchaseLimit(TestValues.GroupId, 5);
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks(product, role: Roles.ReadOnly);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Value.PurchaseLimits.Should().BeEmpty();
    }

    // ── freshUser.GroupId resolution (NOT the stale claim) ──────────

    // The handler resolves the caller's GroupId FRESH from the DB via
    // userRepository.GetByIdAsync — NOT from the GroupId claim on the
    // auth cookie (which is a snapshot from login time and goes stale
    // when an admin reassigns the user's group after they're logged in).
    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_LoadsFreshUserFromUserRepository()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();

        // Act
        await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        await userRepo.Received(1).GetByIdAsync(
            Arg.Is<Guid>(id => id == TestValues.CreatedByUserId),
            Arg.Any<CancellationToken>());
    }

    // ── MyPurchaseLimit via policy (honors LimitMode) ────────────────

    // The handler routes MyPurchaseLimit through
    // IPurchaseLimitPolicy.GetCountLimitAsync — this honors the
    // LimitMode (CountOnly/SalaryOnly/Both). Returns null when
    // SalaryOnly mode (the limit is not enforced).
    [Fact]
    public async Task HandleAsync_WhenPolicyReturnsLimit_ForwardsItToDtoMyPurchaseLimit()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();
        // Override the default: policy returns 7 (LimitMode=CountOnly,
        // customer has limit=7 for this product).
        purchaseLimitPolicy.GetCountLimitAsync(default, default, default)
            .ReturnsForAnyArgs(7);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Value.MyPurchaseLimit.Should().Be(7);
    }

    [Fact]
    public async Task HandleAsync_WhenPolicyReturnsNull_SetsDtoMyPurchaseLimitToNull()
    {
        // Arrange
        // Default: policy returns null (SalaryOnly mode, or staff user
        // with GroupId=null, or product has no limit for this group).
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Value.MyPurchaseLimit.Should().BeNull();
    }

    // The handler routes MyPurchaseLimit through the policy — NOT
    // directly to product.GetPurchaseLimitForGroup. The previous (buggy)
    // implementation called product.GetPurchaseLimitForGroup(groupId)?.Limit
    // directly, bypassing the policy and returning the configured limit
    // even when LimitMode was SalaryOnly. The Step 12-c fix routes
    // through the policy. We verify the policy IS called.
    [Fact]
    public async Task HandleAsync_WhenAllChecksPass_CallsPolicyGetCountLimitAsync()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, product) = BuildMocks();

        // Act
        await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        await purchaseLimitPolicy.Received(1).GetCountLimitAsync(
            Arg.Is<Guid>(id => id == product.Id),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>());
    }

    // ── MoneyDto population ──────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenProductHasPrice_PopulatesMoneyDto()
    {
        // Arrange
        // ProductBuilder's default price = Money(100m, USD).
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Value.Price.Should().NotBeNull();
        result.Value.Price.Amount.Should().Be(100m);
        result.Value.Price.Currency.Should().Be(TestValues.USD);
    }

    // ── Product fields projected verbatim ───────────────────────────

    [Fact]
    public async Task HandleAsync_WhenProductExists_ProjectsIdNameDescriptionPictureUrlStock()
    {
        // Arrange
        // Build a product with known values via the ProductBuilder.
        var product = new ProductBuilder()
            .WithName("Custom Name")
            .WithDescription("Custom Description")
            .WithStock(42)
            .WithPictureUrl("https://example.com/pic.png")
            .WithPrice(new Money(99.99m, TestValues.EUR))
            .Build();
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks(product);

        // Act
        var result = await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        result.Value.Id.Should().Be(product.Id);
        result.Value.Name.Should().Be("Custom Name");
        result.Value.Description.Should().Be("Custom Description");
        result.Value.PictureUrl.Should().Be("https://example.com/pic.png");
        result.Value.StockQuantity.Should().Be(42);
        result.Value.Price.Amount.Should().Be(99.99m);
        result.Value.Price.Currency.Should().Be(TestValues.EUR);
    }

    // ── Logger invocations ───────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenNotAuthenticated_LogsWarning()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();
        currentUser.IsAuthenticated.Returns(false);

        // Act
        await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // The not-found path logs an INFORMATION (not Warning) — the SUT
    // uses LogInformation for the not-found case. This is documented in
    // the SUT's code as "we use LogInformation here because a missing
    // product is not necessarily an attack — the UI might just be
    // rendering a stale deep-link".
    [Fact]
    public async Task HandleAsync_WhenProductNotFound_LogsInformation()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();
        productRepo.GetByIdAsync(default, default)
            .ReturnsForAnyArgs((Domain.Products.Entities.Product?)null);

        // Act
        await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger,
            CancellationToken.None);

        // Assert
        logger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    // ── Cancellation token forwarding ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToProductRepository()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, ct);

        // Assert
        await productRepo.Received(1).GetByIdAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }

    [Fact]
    public async Task HandleAsync_WhenCalledWithCancellationToken_ForwardsItToUserRepository()
    {
        // Arrange
        var (currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, _) = BuildMocks();
        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        // Act
        await GetProductByIdQueryHandler.HandleAsync(
            BuildValidQuery(), currentUser, productRepo, userRepo, purchaseLimitPolicy, logger, ct);

        // Assert
        await userRepo.Received(1).GetByIdAsync(
            Arg.Any<Guid>(),
            Arg.Is<CancellationToken>(t => t == ct));
    }
}

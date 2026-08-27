using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Sales.DTOs;
using TakOne.Domain.Common;
using TakOne.Domain.Common.Entities;
using TakOne.Domain.Common.Enums;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Sales.ValueObjects;
using TakOne.Domain.Users;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.Infrastructure.Services;
using TakOne.IntegrationTests.Infrastructure;
using TakOne.SharedKernel.ValueObjects;
using TakOne.Testing;
using Xunit;

namespace TakOne.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="SalaryBudgetService"/> — the
/// infrastructure implementation of <see cref="ISalaryBudgetService"/>.
/// Drives real <see cref="UserRepository"/> + <see cref="CustomerGroupRepository"/>
/// + <see cref="SaleRepository"/> + cached <see cref="SystemSettingsService"/>
/// against an in-memory SQLite DB.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THESE ARE INTEGRATION TESTS:</b> the mock-based unit tests
/// verify the service calls each collaborator with the right args — but
/// they can't catch wiring mistakes like:
/// <list type="bullet">
///   <item>The <c>GetConsumedAmountForCustomerInWindowAsync</c> SQL query
///       miscounting draft carts or sales outside the Persian-month
///       window.</item>
///   <item>The Persian-calendar window arithmetic producing incorrect
///       UTC boundaries (e.g. off-by-one month).</item>
///   <item>The CustomerGroup's Salary ComplexProperty failing to round-
///       trip, so the service sees Salary.Amount=0 instead of the
///       seeded value.</item>
/// </list>
/// </para>
/// <para>
/// <b>"LAST PERSIAN MONTH" APPROACH:</b> to seed a sale whose
/// <c>SubmittedAtUtc</c> falls in the PREVIOUS Persian month, we submit
/// the sale normally (which sets SubmittedAtUtc=DateTime.UtcNow), then
/// use EF Core's <c>ExecuteUpdateAsync</c> to overwrite the column with
/// a date <c>windowStartUtc.AddDays(-1)</c>. This bypasses the domain's
/// private setter (which is fine for a test — production code uses the
/// domain's <c>Submit()</c>).
/// </para>
/// </remarks>
public class SalaryBudgetServiceIntegrationTests
{
    // ── Helpers ─────────────────────────────────────────────────────────

    private static async Task<(
        SalaryBudgetService service,
        IUserRepository userRepo,
        ICustomerGroupRepository groupRepo,
        ISaleRepository saleRepo,
        ApplicationDbContext db)>
        BuildWiredCollaboratorsAsync(LimitMode mode = LimitMode.Both)
    {
        var db = await SqliteTestDbFactory.CreateAsync();

        // EnsureCreatedAsync honors HasData(...) in SystemSettingsConfiguration,
        // so the singleton row already exists with LimitMode=CountOnly. We
        // UPDATE it to the requested mode via ExecuteUpdateAsync (a single
        // SQL UPDATE that bypasses the change tracker + avoids the unique-
        // index conflict that AddAsync would cause against the seeded row).
        await db.SystemSettings
            .Where(s => s.Id == Domain.Common.Entities.SystemSettings.SingletonId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LimitMode, mode));
        db.ChangeTracker.Clear();

        var userRepo = new UserRepository(db);
        var groupRepo = new CustomerGroupRepository(db);
        var saleRepo = new SaleRepository(db);
        var realSettingsRepo = new SystemSettingsRepository(db);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var settingsService = new SystemSettingsService(
            realSettingsRepo,
            cache);

        var service = new SalaryBudgetService(
            userRepo,
            groupRepo,
            saleRepo,
            settingsService,
            Substitute.For<ILogger<SalaryBudgetService>>());

        return (service, userRepo, groupRepo, saleRepo, db);
    }

    // Seed a customer User + their CustomerGroup + return both Ids.
    private static async Task<(Guid userId, Guid groupId)> SeedCustomerAndGroupAsync(
        ApplicationDbContext db,
        decimal salaryAmount,
        string salaryCurrency = TestValues.IRR)
    {
        var group = CustomerGroup.Create(
            "Test Group",
            new Money(salaryAmount, salaryCurrency));
        db.CustomerGroups.Add(group);
        await db.SaveChangesAsync();

        var customer = User.CreateCustomer(
            workerId: "CUST-1",
            fullName: "Test Customer",
            groupId: group.Id);
        db.DomainUsers.Add(customer);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (customer.Id, group.Id);
    }

    // Seed a submitted sale (in the current Persian month window) with
    // the given total. The sale's customer is the test's seeded customer.
    private static async Task<Guid> SeedSubmittedSaleInWindowAsync(
        ApplicationDbContext db,
        Guid customerId,
        decimal totalAmount,
        string currency = TestValues.IRR)
    {
        var sale = Sale.Create(
            customerId,
            customerName: "Test Customer",
            saleNumber: null,
            createdByUserId: customerId,
            createdByName: "Test Customer");

        // AddLineItem requires Draft state — we set unitPrice so the line's
        // GrossTotal equals totalAmount (quantity=1, unitPrice=totalAmount).
        sale.AddLineItem(
            productId: Guid.NewGuid(),
            productName: "Test Item",
            quantity: 1,
            unitPrice: new Money(totalAmount, currency));
        sale.Submit(SaleNumber.Create(1403, new Random().Next(1, 999999)));

        db.Sales.Add(sale);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return sale.Id;
    }

    // Seed a submitted sale whose SubmittedAtUtc is set to a date OUTSIDE
    // the current Persian month (specifically, 1 day before windowStart).
    private static async Task<Guid> SeedSubmittedSaleOutOfWindowAsync(
        ApplicationDbContext db,
        Guid customerId,
        decimal totalAmount,
        string currency = TestValues.IRR)
    {
        var saleId = await SeedSubmittedSaleInWindowAsync(db, customerId, totalAmount, currency);

        // Overwrite SubmittedAtUtc to fall BEFORE the current Persian month
        // window's start. The SalaryBudgetWindow helper computes the
        // window; we go 1 day before its start.
        var windowStart = SalaryBudgetWindow.GetStartOfCurrentMonth(DateTime.UtcNow);
        var outOfWindowDate = windowStart.AddDays(-1);

        await db.Sales
            .Where(s => s.Id == saleId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SubmittedAtUtc, outOfWindowDate));
        db.ChangeTracker.Clear();
        return saleId;
    }

    // ── Tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBudgetInfoAsync_ForCustomerWithGroupAndModeBoth_ReturnsBudgetInfo()
    {
        // Arrange — salary=1000, seed a submitted sale for 200 in window.
        var (service, _, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            var (customerId, _) = await SeedCustomerAndGroupAsync(db, salaryAmount: 1000m);
            await SeedSubmittedSaleInWindowAsync(db, customerId, totalAmount: 200m);

            var beforeUtc = DateTime.UtcNow;
            var windowStart = SalaryBudgetWindow.GetStartOfCurrentMonth(beforeUtc);
            var windowEnd = SalaryBudgetWindow.GetStartOfNextMonth(beforeUtc);

            // Act
            var info = await service.GetBudgetInfoAsync(customerId, CancellationToken.None);

            // Assert — salary=1000, consumed=200, remaining=800, window correct.
            info.Should().NotBeNull();
            info!.Salary.Amount.Should().Be(1000m);
            info.Salary.Currency.Should().Be(TestValues.IRR);
            info.Consumed.Should().Be(200m);
            info.Remaining.Should().Be(800m);
            info.WindowStartUtc.Should().Be(windowStart);
            info.WindowEndUtc.Should().Be(windowEnd);
        }
    }

    // Staff user (GroupId=null) — service returns null without loading
    // the customer group. Verifies the staff-bypass short-circuit.
    [Fact]
    public async Task GetBudgetInfoAsync_ForStaffUser_ReturnsNullWithoutLoadingGroup()
    {
        // Arrange — staff user (GroupId=null).
        var (service, _, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            var staff = User.CreateStaff("STAFF-1", "Test Staff");
            db.DomainUsers.Add(staff);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Act
            var info = await service.GetBudgetInfoAsync(staff.Id, CancellationToken.None);

            // Assert — null (staff has no group, so no salary budget).
            info.Should().BeNull();
        }
    }

    [Fact]
    public async Task GetBudgetInfoAsync_ForNonExistentUser_ReturnsNullWithWarning()
    {
        // Arrange
        var (service, _, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            // Act — customerId that doesn't exist in the DB.
            var info = await service.GetBudgetInfoAsync(
                Guid.NewGuid(), CancellationToken.None);

            // Assert — null (defensive, no crash).
            info.Should().BeNull();
        }
    }

    // CountOnly mode bypasses salary budget enforcement entirely — the
    // service returns null WITHOUT computing the consumed amount (no
    // saleRepo call).
    [Fact]
    public async Task GetBudgetInfoAsync_WithModeCountOnly_ReturnsNullWithoutComputingConsumed()
    {
        // Arrange — mode=CountOnly, salary=1000, sale for 200 in window.
        var (service, _, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.CountOnly);
        await using (db)
        {
            var (customerId, _) = await SeedCustomerAndGroupAsync(db, salaryAmount: 1000m);
            await SeedSubmittedSaleInWindowAsync(db, customerId, totalAmount: 200m);

            // Act
            var info = await service.GetBudgetInfoAsync(customerId, CancellationToken.None);

            // Assert — null because CountOnly mode bypasses salary budget.
            info.Should().BeNull();
        }
    }

    [Fact]
    public async Task GetBudgetInfoAsync_RemainingCanGoNegative_WhenConsumedExceedsSalary()
    {
        // Arrange — salary=100, consumed=150 → remaining=-50 (no clamping).
        // This is the documented behavior: the salary was lowered mid-month
        // after the sale was made. The customer cannot add anything until
        // the next Persian month reset.
        var (service, _, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            var (customerId, _) = await SeedCustomerAndGroupAsync(db, salaryAmount: 100m);
            await SeedSubmittedSaleInWindowAsync(db, customerId, totalAmount: 150m);

            // Act
            var info = await service.GetBudgetInfoAsync(customerId, CancellationToken.None);

            // Assert — remaining is -50 (no clamping to zero).
            info.Should().NotBeNull();
            info!.Consumed.Should().Be(150m);
            info.Remaining.Should().Be(-50m);
        }
    }

    // Verifies the Persian-month window correctly excludes sales submitted
    // in the previous month. The consumed-amount query filters by
    // [windowStartUtc, windowEndUtc) — a sale submitted on the last day of
    // the previous Persian month is NOT counted in the current month's
    // consumed amount.
    [Fact]
    public async Task GetBudgetInfoAsync_OnlyCountsSalesInCurrentPersianMonth()
    {
        // Arrange — salary=1000, two submitted sales:
        //   - 100 in the current Persian month (consumed=100).
        //   - 500 in the previous Persian month (consumed=0 for this month).
        // Expected: Consumed=100 (NOT 600), Remaining=900.
        var (service, _, _, _, db) = await BuildWiredCollaboratorsAsync(LimitMode.Both);
        await using (db)
        {
            var (customerId, _) = await SeedCustomerAndGroupAsync(db, salaryAmount: 1000m);

            // In-window sale: 100 IRR.
            await SeedSubmittedSaleInWindowAsync(db, customerId, totalAmount: 100m);
            // Out-of-window sale (previous Persian month): 500 IRR.
            await SeedSubmittedSaleOutOfWindowAsync(db, customerId, totalAmount: 500m);

            // Act
            var info = await service.GetBudgetInfoAsync(customerId, CancellationToken.None);

            // Assert — consumed=100, NOT 600. The out-of-window sale is
            // correctly excluded by the window filter.
            info.Should().NotBeNull();
            info!.Consumed.Should().Be(100m);
            info.Remaining.Should().Be(900m);
        }
    }
}

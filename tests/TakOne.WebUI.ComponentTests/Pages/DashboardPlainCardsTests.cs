using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Dashboard.DTOs;
using TakOne.Application.Dashboard.Queries.GetDashboardStats;
using TakOne.Application.Resources;
using TakOne.SharedKernel.Common;
using TakOne.WebUI.Components.Pages.Dashboard;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit tests pinning the dashboard cards' PLAIN rendering:
/// the KPI cards (incl. Pending Approvals) are non-interactive <c>div</c>s
/// with no link affordances, and the Recent Orders rows are plain data
/// rows with no click/keyboard navigation.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS EXISTS</b>: a previous round turned the Pending-Approvals
/// KPI card into a whole-card anchor (chevron badge, hover/focus link
/// styles) and made the Recent Orders rows clickable — unrequested style
/// changes to existing cards, which the product owner asked to have
/// UNDONE. These tests pin the restored state so the affordances cannot
/// silently creep back in.
/// </para>
/// <para>
/// <b>WIRING</b>: same harness family as
/// <see cref="DashboardRestockAlertTests"/> — full page mount with the
/// fake bus returning a hand-built DTO; navigation is observed through
/// bUnit's <see cref="FakeNavigationManager"/>. The chart JS interop
/// runs under bUnit's Loose mode.
/// </para>
/// </remarks>
public class DashboardPlainCardsTests
{
    private static (TestContext ctx, DashboardStatsDto dto) Setup(params RecentOrderDto[] recentOrders)
    {
        var dto = new DashboardStatsDto
        {
            CurrentUserName = "Test User",
            IsEmployeeScoped = false,
            DisplayCurrency = "تومان",
            IsToman = true,
            TodayOrdersCount = 3,
            PendingSalesCount = 4,
            ThisWeekRevenue = new List<WeeklyRevenueDto>(),
            LastWeekRevenue = new List<WeeklyRevenueDto>(),
            StatusBreakdown = new List<StatusCountDto>(),
            TopProducts = new List<TopProductDto>(),
            TopCategories = new List<CategorySalesCountDto>(),
            TopEmployees = new List<TopEmployeeDto>(),
            RecentOrders = recentOrders.ToList(),
            YearlyData = new List<YearlyRevenueDto>(),
            CurrentYearMonthlyData = new List<MonthlyRevenueDto>(),
            LowStockProducts = new List<LowStockProductDto>(),
            LowStockProductTotalCount = 0
        };

        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Local twin of ComponentTestSetup.AddIdentityLocalizer that ALSO
        // stubs the format-args indexer (several dashboard strings format
        // args; the shared helper only stubs the key-only indexer).
        var dashLoc = Substitute.For<IStringLocalizer<Dashboard>>();
        dashLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        dashLoc[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(dashLoc);

        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));

        // Admin user — the full bUnit authorization pipeline.
        ComponentTestSetup.AddBunitAuthorizedUser(ctx, "tester", Roles.Admin);

        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.NewGuid());
        currentUser.FullName.Returns("Test User");
        ctx.Services.AddSingleton(currentUser);

        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<Result<DashboardStatsDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<DashboardStatsDto>.Success(dto));
        ctx.Services.AddSingleton(bus);

        return (ctx, dto);
    }

    private static RecentOrderDto MakeOrder(Guid saleId, string saleNumber) => new()
    {
        SaleId = saleId,
        SaleNumber = saleNumber,
        CustomerName = "Test Customer",
        ProductSummary = "Golden Pen × 2",
        TotalAmount = 250_000m,
        Status = "Pending",
        SubmittedAtUtc = DateTime.UtcNow.AddMinutes(-10)
    };

    [Fact]
    public void Dashboard_KpiCards_RenderAsPlainNonInteractiveDivs()
    {
        // Arrange / Act
        var (ctx, _) = Setup();
        using (ctx)
        {
            var cut = ctx.RenderComponent<Dashboard>();

            // Assert — the KPI grid renders its four cards as plain divs.
            cut.WaitForAssertion(
                () => cut.FindAll("div.tm-dash-kpi-card").Should().HaveCount(4),
                TimeSpan.FromSeconds(10));

            // None of the cards is an anchor or carries link affordances:
            // no whole-card link modifier, no chevron badge, no sr-only
            // destination text (the Pending-Approvals card previously
            // carried all three — reverted at the product owner's request).
            cut.FindAll("a.tm-dash-kpi-card").Should().BeEmpty(
                "KPI cards are plain divs, not links");
            cut.FindAll(".tm-dash-kpi-card--link").Should().BeEmpty();
            cut.FindAll(".tm-dash-kpi-link-chevron").Should().BeEmpty();
            cut.FindAll(".tm-sr-only").Should().BeEmpty();
            cut.Markup.Should().NotContain("Kpi_PendingApprovalsLink",
                "the sr-only destination key must stay retired");
            cut.Markup.Should().Contain("Kpi_PendingApprovals",
                "the visible metric label still renders");
        }
    }

    [Fact]
    public void Dashboard_RecentOrderRows_ArePlainRows_NoClickNavigation()
    {
        // Arrange — two recent orders with known ids.
        var firstSaleId = Guid.NewGuid();
        var secondSaleId = Guid.NewGuid();
        var (ctx, _) = Setup(
            MakeOrder(firstSaleId, "TM-1001"),
            MakeOrder(secondSaleId, "TM-1002"));
        using (ctx)
        {
            var cut = ctx.RenderComponent<Dashboard>();

            // Assert — both rows render as plain data rows.
            cut.WaitForAssertion(
                () => cut.FindAll(".tm-dash-table tbody tr").Should().HaveCount(2),
                TimeSpan.FromSeconds(10));
            var rows = cut.FindAll(".tm-dash-table tbody tr");
            foreach (var row in rows)
            {
                row.ClassList.Should().NotContain("tm-dash-recent-row");
                row.GetAttribute("tabindex").Should().BeNull(
                    "plain rows are not keyboard-focusable controls");
                row.GetAttribute("title").Should().BeNull(
                    "plain rows carry no destination tooltip");
                row.GetAttribute("blazor:onclick").Should().BeNull(
                    "plain rows carry no click handler — bUnit's Click() would " +
                    "throw MissingEventHandlerException on them, which is the " +
                    "revert's own proof");
            }

            // No interaction with the rows exists — the URI is untouched
            // without any event dispatch (nothing to dispatch).
            var nav = ctx.Services.GetRequiredService<FakeNavigationManager>();
            nav.Uri.Should().NotEndWith("/Sales/",
                "nothing on the dashboard's recent-orders table navigates");
        }
    }
}

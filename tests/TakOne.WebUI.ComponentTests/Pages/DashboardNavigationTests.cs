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
/// Page-level bUnit tests for the Dashboard's new navigation surfaces:
/// the Pending-Approvals KPI whole-card deep link and the clickable
/// Recent Orders rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. The Pending-Approvals KPI card renders as a REAL anchor (not a
///      div) whose href is the Sales page's URL state contract
///      (<c>/Sales?status=Pending</c> — built from
///      <c>nameof(SaleStatus.Pending)</c>, no stale literal), carries
///      the chevron affordance, and appends the screen-reader-only
///      destination text to its accessible name.
///   2. Recent Orders rows are keyboard-focusable (tabindex) and CLICK
///      navigates to <c>/Sales/{SaleId}</c> — the same route the Sales
///      grid's sale-number links use.
///   3. Enter opens the row; other keys (Space/page-scroll, Tab/focus
///      movement) are left at their browser defaults — the row never
///      hijacks the keyboard.
/// </para>
/// <para>
/// <b>WIRING</b>: same harness as
/// <see cref="DashboardRestockAlertTests"/> — full page mount with the
/// fake bus returning a hand-built DTO; navigation is observed through
/// bUnit's <see cref="FakeNavigationManager"/>. The chart JS interop
/// runs under bUnit's Loose mode.
/// </para>
/// </remarks>
public class DashboardNavigationTests
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
    public void Dashboard_PendingKpiCard_IsDeepLinkToPendingQueue()
    {
        // Arrange / Act
        var (ctx, _) = Setup();
        using (ctx)
        {
            var cut = ctx.RenderComponent<Dashboard>();

            // Assert — the KPI card is a real ANCHOR with the Sales
            // page's URL state contract pre-applied (the page's
            // "Pending only" chip derives its active state from this
            // restored filter), plus the chevron affordance and the
            // sr-only destination text for assistive tech.
            cut.WaitForAssertion(
                () => cut.FindAll("a.tm-dash-kpi-card--link").Should().HaveCount(1),
                TimeSpan.FromSeconds(10));
            var link = cut.Find("a.tm-dash-kpi-card--link");
            link.GetAttribute("href").Should().Be("/Sales?status=Pending");
            cut.Find(".tm-dash-kpi-link-chevron").Should().NotBeNull();
            cut.Markup.Should().Contain("Kpi_PendingApprovalsLink",
                "the sr-only span appends the destination to the anchor's accessible name");
            cut.Markup.Should().Contain("Kpi_PendingApprovals",
                "the visible metric label renders");
        }
    }

    [Fact]
    public void Dashboard_RecentOrderRow_ClickNavigatesToSaleDetail()
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

            // Assert — both rows render as clickable, focusable rows.
            cut.WaitForAssertion(
                () => cut.FindAll("tr.tm-dash-recent-row").Should().HaveCount(2),
                TimeSpan.FromSeconds(10));
            var rows = cut.FindAll("tr.tm-dash-recent-row");
            rows[0].GetAttribute("tabindex").Should().Be("0",
                "keyboard users can focus the row");
            rows[0].GetAttribute("title").Should().NotBeNullOrEmpty(
                "the tooltip announces the destination before the click");

            // Act — click the FIRST row.
            rows[0].Click();

            // Assert — navigation went to that sale's detail page (the
            // same route the Sales grid's sale-number links use).
            var nav = ctx.Services.GetRequiredService<FakeNavigationManager>();
            nav.Uri.Should().EndWith($"/Sales/{firstSaleId}");
        }
    }

    [Fact]
    public void Dashboard_RecentOrderRow_EnterKeyNavigates_OtherKeysDoNot()
    {
        // Arrange
        var firstSaleId = Guid.NewGuid();
        var secondSaleId = Guid.NewGuid();
        var (ctx, _) = Setup(
            MakeOrder(firstSaleId, "TM-1001"),
            MakeOrder(secondSaleId, "TM-1002"));
        using (ctx)
        {
            var cut = ctx.RenderComponent<Dashboard>();
            cut.WaitForAssertion(
                () => cut.FindAll("tr.tm-dash-recent-row").Should().HaveCount(2),
                TimeSpan.FromSeconds(10));
            var nav = ctx.Services.GetRequiredService<FakeNavigationManager>();
            var initialUri = nav.Uri;

            // Act — a non-Enter key: the row must NOT hijack it (Space
            // scrolls the page, Tab moves focus — browser defaults).
            // NOTE: rows are RE-FOUND before each dispatch — Blazor
            // re-renders the component after every event dispatch, which
            // rebinds the handlers; a stale captured element would fail
            // the renderer's event-binding lookup.
            cut.FindAll("tr.tm-dash-recent-row")[0].KeyDown("a");
            cut.WaitForAssertion(() => cut.FindAll("tr.tm-dash-recent-row").Should().HaveCount(2),
                TimeSpan.FromSeconds(5));
            nav.Uri.Should().Be(initialUri, "a non-Enter key must not navigate");

            // Act — Enter on the SECOND row.
            cut.FindAll("tr.tm-dash-recent-row")[1].KeyDown("Enter");

            // Assert — navigation went to that row's sale.
            nav.Uri.Should().EndWith($"/Sales/{secondSaleId}");
        }
    }
}

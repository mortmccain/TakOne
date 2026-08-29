using Bunit;
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
/// Page-level bUnit test for the desktop Dashboard's Round-5 period
/// selector — the first component test for either dashboard page.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>: the selector renders, its initial load
/// dispatches a window-free query, clicking a preset re-dispatches with
/// the [FromUtc, ToUtc) window (Tehran-midnight aligned, 7 days wide),
/// and the returned period-scoped DTO flips the KPI cards to their
/// period variants. The chart JS interop runs under bUnit's Loose mode.
/// </para>
/// </remarks>
public class DashboardPeriodSelectorTests
{
    private static DashboardStatsDto MakeDto(bool periodScoped = false) => new()
    {
        CurrentUserName = "Test Admin",
        IsEmployeeScoped = false,
        DisplayCurrency = "تومان",
        IsToman = true,
        TodayOrdersCount = 3,
        PeriodOrdersCount = periodScoped ? 12 : 0,
        PreviousPeriodOrdersCount = periodScoped ? 5 : 0,
        IsPeriodScoped = periodScoped,
        ThisWeekRevenue = new List<WeeklyRevenueDto>(),
        LastWeekRevenue = new List<WeeklyRevenueDto>(),
        StatusBreakdown = new List<StatusCountDto>(),
        TopProducts = new List<TopProductDto>(),
        TopCategories = new List<CategorySalesCountDto>(),
        TopEmployees = new List<TopEmployeeDto>(),
        RecentOrders = new List<RecentOrderDto>(),
        YearlyData = new List<YearlyRevenueDto>(),
        CurrentYearMonthlyData = new List<MonthlyRevenueDto>()
    };

    private static (TestContext ctx, IMessageBus bus) Setup()
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        ComponentTestSetup.AddIdentityLocalizer<Dashboard>(ctx);

        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));

        // Full bUnit authorization pipeline (the page contains an
        // <AuthorizeView> for the top-products "View All" link, which needs
        // IAuthorizationPolicyProvider etc. — AddBunitAuthorizedUser wires
        // all of it AND the cascading Task<AuthenticationState>).
        ComponentTestSetup.AddBunitAuthorizedUser(ctx, "tester", Roles.Admin);

        // ICurrentUserService: an authenticated admin (the page injects it;
        // the handler resolves roles server-side, but the page itself only
        // needs the service present).
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.NewGuid());
        currentUser.FullName.Returns("Test Admin");
        ctx.Services.AddSingleton(currentUser);

        // The fake bus: returns a period-scoped DTO when the query carries
        // a window, the fixed-anchor DTO otherwise — so the page's KPI
        // branches actually flip when a preset is clicked.
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<Result<DashboardStatsDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<GetDashboardStatsQuery>(0);
                return Result<DashboardStatsDto>.Success(MakeDto(query.FromUtc.HasValue));
            });
        ctx.Services.AddSingleton(bus);

        return (ctx, bus);
    }

    private static IRenderedComponent<Dashboard> Render(TestContext ctx)
        => ctx.RenderComponent<Dashboard>();

    [Fact]
    public async Task Dashboard_Mount_DispatchesWindowFreeQuery_AndRendersFixedAnchorKpis()
    {
        var (ctx, bus) = Setup();
        using (ctx)
        {
            var cut = Render(ctx);

            // The dashboard settles with the fixed-anchor KPI labels.
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("Kpi_TodayOrders"),
                TimeSpan.FromSeconds(10));

            // The initial query carries NO period window.
            await bus.Received(1).InvokeAsync<Result<DashboardStatsDto>>(
                Arg.Is<GetDashboardStatsQuery>(q => q.FromUtc == null && q.ToUtc == null),
                Arg.Any<CancellationToken>());

            // The selector renders all four options.
            cut.FindAll(".tm-dash-period-btn").Should().HaveCount(4,
                "Current + 7/30/90-day presets");
        }
    }

    [Fact]
    public async Task Dashboard_ClickSevenDayPreset_DispatchesWindowAndFlipsKpis()
    {
        var (ctx, bus) = Setup();
        using (ctx)
        {
            var cut = Render(ctx);
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("Kpi_TodayOrders"),
                TimeSpan.FromSeconds(10));

            // Act — click the "Last 7 days" preset (the identity localizer
            // stub renders the raw key as the button text).
            var sevenDayChip = cut.FindAll(".tm-dash-period-btn")
                .First(b => b.TextContent.Trim() == "Period_7Days");
            await cut.InvokeAsync(() => sevenDayChip.Click());

            // Assert — a second query with a 7-day, midnight-aligned window.
            await bus.Received(1).InvokeAsync<Result<DashboardStatsDto>>(
                Arg.Is<GetDashboardStatsQuery>(q =>
                    q.FromUtc.HasValue &&
                    q.ToUtc.HasValue &&
                    (q.ToUtc.Value - q.FromUtc.Value) == TimeSpan.FromDays(7)),
                Arg.Any<CancellationToken>());

            // And the KPI cards flipped to their period variants (the fake
            // bus returns IsPeriodScoped=true for windowed queries).
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("Kpi_PeriodOrders"),
                TimeSpan.FromSeconds(10));
            cut.Markup.Should().Contain("KpiDeltaVsPreviousPeriod",
                "the period delta chips read 'vs previous period'");
        }
    }

    [Fact]
    public async Task Dashboard_ClickCurrentAfterPreset_DispatchesWindowFreeQueryAgain()
    {
        var (ctx, bus) = Setup();
        using (ctx)
        {
            var cut = Render(ctx);
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("Kpi_TodayOrders"),
                TimeSpan.FromSeconds(10));

            // Switch to 30 days, then back to Current.
            var thirtyDayChip = cut.FindAll(".tm-dash-period-btn")
                .First(b => b.TextContent.Trim() == "Period_30Days");
            await cut.InvokeAsync(() => thirtyDayChip.Click());
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("Kpi_PeriodOrders"),
                TimeSpan.FromSeconds(10));

            var currentChip = cut.FindAll(".tm-dash-period-btn")
                .First(b => b.TextContent.Trim() == "Period_Current");
            await cut.InvokeAsync(() => currentChip.Click());

            // Two window-free queries total: the initial load + the switch
            // back to Current.
            await bus.Received(2).InvokeAsync<Result<DashboardStatsDto>>(
                Arg.Is<GetDashboardStatsQuery>(q => q.FromUtc == null),
                Arg.Any<CancellationToken>());

            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("Kpi_TodayOrders"),
                TimeSpan.FromSeconds(10));
        }
    }
}

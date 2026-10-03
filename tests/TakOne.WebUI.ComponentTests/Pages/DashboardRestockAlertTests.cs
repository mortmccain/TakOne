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
/// Page-level bUnit test for the Dashboard's restock alert — the
/// low-stock banner between the KPI grid and the charts.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>: a company-wide dashboard renders the banner
/// from <see cref="DashboardStatsDto.LowStockProducts"/> (one chip per
/// product, out-of-stock chips carrying the danger modifier + the admin
/// "manage products" link), an employee-scoped dashboard hides it even
/// when the DTO carries data (defense-in-depth — the handler skips the
/// query, the UI independently refuses to render it), and a healthy
/// catalog shows no banner at all.
/// </para>
/// <para>
/// <b>WIRING</b>: same harness as
/// <see cref="DashboardPeriodSelectorTests"/> — full page mount with the
/// fake bus returning a hand-built DTO; the chart JS interop runs under
/// bUnit's Loose mode.
/// </para>
/// </remarks>
public class DashboardRestockAlertTests
{
    private static DashboardStatsDto MakeDto(
        bool employeeScoped,
        int lowStockTotalCount = 0,
        params LowStockProductDto[] lowStock) => new()
    {
        CurrentUserName = "Test User",
        IsEmployeeScoped = employeeScoped,
        DisplayCurrency = "تومان",
        IsToman = true,
        TodayOrdersCount = 3,
        ThisWeekRevenue = new List<WeeklyRevenueDto>(),
        LastWeekRevenue = new List<WeeklyRevenueDto>(),
        StatusBreakdown = new List<StatusCountDto>(),
        TopProducts = new List<TopProductDto>(),
        TopCategories = new List<CategorySalesCountDto>(),
        TopEmployees = new List<TopEmployeeDto>(),
        RecentOrders = new List<RecentOrderDto>(),
        YearlyData = new List<YearlyRevenueDto>(),
        CurrentYearMonthlyData = new List<MonthlyRevenueDto>(),
        LowStockProducts = lowStock.ToList(),
        LowStockProductTotalCount = lowStockTotalCount
    };

    private static (TestContext ctx, IMessageBus bus) Setup(DashboardStatsDto dto)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Local twin of ComponentTestSetup.AddIdentityLocalizer that ALSO
        // stubs the format-args indexer (the restock subtitle uses
        // Loc["Restock_SubCapped", total, shown] — the shared helper only
        // stubs the key-only indexer, which renders args-formatted strings
        // as empty). string.Format with a placeholder-free key returns the
        // key itself, so the args stub renders just the key.
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

        // Admin user — the full bUnit authorization pipeline (the banner's
        // "manage products" link sits in an <AuthorizeView>).
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

        return (ctx, bus);
    }

    [Fact]
    public void Dashboard_CompanyWideWithLowStock_RendersRestockAlert()
    {
        // Arrange — most-critical-first DTO (out-of-stock leads); the
        // uncapped total EQUALS the shown count (nothing hidden).
        var dto = MakeDto(
            employeeScoped: false,
            lowStockTotalCount: 2,
            new LowStockProductDto { Id = Guid.NewGuid(), Name = "Golden Pen", StockQuantity = 0 },
            new LowStockProductDto { Id = Guid.NewGuid(), Name = "Leather Notebook", StockQuantity = 7 });
        var (ctx, _) = Setup(dto);
        using (ctx)
        {
            // Act
            var cut = ctx.RenderComponent<Dashboard>();

            // Assert — the banner renders with its title + the count in
            // the subtitle, one chip per product (name + stock count).
            cut.WaitForAssertion(
                () => cut.FindAll(".tm-dash-restock-chip").Should().HaveCount(2),
                TimeSpan.FromSeconds(10));
            cut.Markup.Should().Contain("Restock_Title");

            // Equal totals render the plain subtitle (NOT the capped
            // variant) — nothing is hidden beyond the chips.
            cut.Markup.Should().Contain("Restock_Sub");
            cut.Markup.Should().NotContain("Restock_SubCapped");

            var chips = cut.FindAll(".tm-dash-restock-chip");
            chips[0].ClassList.Should().Contain("tm-dash-restock-chip-out",
                "the out-of-stock chip carries the danger modifier");
            chips[0].TextContent.Should().Contain("Golden Pen").And.Contain("0");
            chips[1].ClassList.Should().NotContain("tm-dash-restock-chip-out");
            chips[1].TextContent.Should().Contain("Leather Notebook").And.Contain("7");

            // The admin sees the "manage products" link (inside the
            // AuthorizeView; the Admin role passes the gate) — and it
            // deep-links with the low-stock filter PRE-APPLIED via the
            // products page's URL state contract (stockn/stocknop).
            cut.Markup.Should().Contain("Restock_Manage");
            var link = cut.Find(".tm-dash-restock-link");
            link.GetAttribute("href").Should().Be(
                "/Admin/Products?stockn=10&stocknop=LessThanOrEqual");
        }
    }

    [Fact]
    public void Dashboard_MoreLowStockThanChipCap_SubtitleShowsTrueTotal()
    {
        // Arrange — 9 low-stock products in reality, 6 shown (the DTO's
        // cap): the subtitle must say 9 — showing the 6 most critical —
        // NOT the capped count that reads as "only 6".
        var shown = Enumerable.Range(1, 6)
            .Select(i => new LowStockProductDto
            {
                Id = Guid.NewGuid(),
                Name = $"Product {i}",
                StockQuantity = i
            })
            .ToArray();
        var dto = MakeDto(employeeScoped: false, lowStockTotalCount: 9, shown);
        var (ctx, _) = Setup(dto);
        using (ctx)
        {
            // Act
            var cut = ctx.RenderComponent<Dashboard>();

            // Assert — the CAPPED subtitle variant with both numbers:
            // the stub localizer renders the key + args, so both the
            // total (9) and the shown count (6) appear in the subtitle.
            cut.WaitForAssertion(
                () => cut.FindAll(".tm-dash-restock-chip").Should().HaveCount(6),
                TimeSpan.FromSeconds(10));
            var subtitle = cut.Find(".tm-dash-restock-sub");
            // The stub localizer renders keys verbatim (string.Format on a
            // placeholder-free key), so the CAPPED variant's key — and
            // only it — appears in the rendered subtitle.
            subtitle.TextContent.Should().Contain("Restock_SubCapped");
        }
    }

    [Fact]
    public void Dashboard_EmployeeScoped_HidesRestockAlertEvenWithData()
    {
        // Arrange — the handler never populates LowStockProducts for
        // employees, but the UI gate is independent (defense-in-depth):
        // if a future change leaks stock data into an employee DTO, the
        // banner must STILL not render.
        var dto = MakeDto(
            employeeScoped: true,
            lowStock: new LowStockProductDto { Id = Guid.NewGuid(), Name = "Golden Pen", StockQuantity = 0 });
        var (ctx, _) = Setup(dto);
        using (ctx)
        {
            // Act
            var cut = ctx.RenderComponent<Dashboard>();

            // Assert — the dashboard itself renders (KPI cards settle)
            // but the restock banner is absent.
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("Kpi_TodayOrders"),
                TimeSpan.FromSeconds(10));
            cut.FindAll(".tm-dash-restock").Should().BeEmpty();
            cut.FindAll(".tm-dash-restock-chip").Should().BeEmpty();
        }
    }

    [Fact]
    public void Dashboard_HealthyCatalog_HidesRestockAlert()
    {
        // Arrange — company-wide scope, nothing low: no banner (the
        // alert earns its screen space — silence when all is well).
        var dto = MakeDto(employeeScoped: false);
        var (ctx, _) = Setup(dto);
        using (ctx)
        {
            // Act
            var cut = ctx.RenderComponent<Dashboard>();

            // Assert
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("Kpi_TodayOrders"),
                TimeSpan.FromSeconds(10));
            cut.FindAll(".tm-dash-restock").Should().BeEmpty();
        }
    }
}

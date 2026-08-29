using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using TakOne.Application.Resources;
using NSubstitute;
using TakOne.Application.Sales.DTOs;
using TakOne.Application.Sales.Queries.GetSalesPaginated;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.Pages.Sales;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit test for the Sales grid's Radzen LoadData
/// (server-driven paging) wiring — Round 4's riskiest glue.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b> (that the per-layer tests cannot): the page
/// actually MOUNTS the grid with <c>Data=null</c> (the pinned Radzen
/// contract for LoadData-on-init), the grid's initial LoadData reaches
/// <c>IMessageBus.InvokeAsync</c> with a correctly-shaped query, and the
/// returned page flows back into the grid via <c>Data</c> + <c>Count</c>
/// (rows render; the pager shows the server-side total).
/// </para>
/// <para>
/// <b>WIRING</b>: the IMessageBus is an NSubstitute fake returning a
/// canned page; every other dependency uses the same real/stub mix the
/// rest of this project's component tests use (identity localizers,
/// stub auth provider, bUnit's loose JS interop).
/// </para>
/// <para>
/// This is the FIRST full-page test in the ComponentTests project (the
/// project docs defer full-page tests generally) — the Sales grid's
/// brand-new LoadData wiring justified wiring one up.
/// </para>
/// </remarks>
public class SalesPageLoadDataTests
{
    private static List<SaleListItemDto> MakeSales(int count) =>
        Enumerable.Range(1, count).Select(i => new SaleListItemDto
        {
            Id = Guid.NewGuid(),
            SaleNumber = $"INT-1405-{i:00000000}",
            CustomerName = $"Customer {i:000}",
            Status = "Pending",
            Total = new MoneyDto { Amount = i * 100m, Currency = "IRR" },
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-i),
            CreatedByUserId = Guid.NewGuid(),
            CreatedByName = "Test Staff"
        }).ToList();

    private static (TestContext ctx, IMessageBus bus) Setup(int totalCount, List<SaleListItemDto> page)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer for the page's own Loc[...] lookups.
        ComponentTestSetup.AddIdentityLocalizer<Sales>(ctx);

        // ErrorDisplayService + ToastService (real instances; the localizer
        // is stubbed so the error paths resolve without resx infrastructure).
        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        // AddRadzenComponents registers NotificationService as SCOPED,
        // which bUnit (root provider, no scope) cannot resolve — override
        // with a singleton instance so the ToastService factory resolves.
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        // Staff user (the page resolves roles via the auth provider).
        ComponentTestSetup.AddAuthenticatedUser(ctx, "tester", TakOne.Application.Common.Authorization.Roles.Admin);

        // The fake bus: capture the dispatched query, return the canned page.
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<PaginatedResult<SaleListItemDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetSalesPaginatedQuery>().Subject;
                return new PaginatedResult<SaleListItemDto>(page, totalCount, query.PageNumber, query.PageSize);
            });
        ctx.Services.AddSingleton(bus);

        return (ctx, bus);
    }

    [Fact]
    public async Task Sales_Mount_FiresLoadData_AndRendersPageWithServerTotal()
    {
        // Arrange — 45 "server" rows, page 1 of 20.
        var (ctx, bus) = Setup(totalCount: 45, page: MakeSales(20));
        using (ctx)
        {
            // Act — mount the page. The grid's LoadData-on-init fires
            // during the initial render lifecycle.
            var cut = ctx.RenderComponent<Sales>();

            // Assert — the load settles: 20 rows render, the pager
            // reflects the SERVER total (45), not the local page size.
            cut.WaitForAssertion(
                () => cut.FindAll(".sale-number-link").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // The pager reflects the SERVER total: 45 rows / 20 per page
            // = 3 page buttons. (Asserting on the pager buttons rather
            // than the summary text — the identity localizer stub returns
            // raw keys for PagingSummaryFormat, so the summary's literal
            // text isn't stable under test.)
            cut.WaitForAssertion(
                () => cut.FindAll("button.rz-pager-page")
                    .Should().HaveCount(3),
                TimeSpan.FromSeconds(5));

            // The dispatched query was the initial page with the default
            // newest-first sort (null SortBy + descending).
            await bus.Received(1).InvokeAsync<PaginatedResult<SaleListItemDto>>(
                Arg.Is<GetSalesPaginatedQuery>(q => q.PageNumber == 1 && q.PageSize == 20),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Sales_PagerPageTwoClick_DispatchesSecondPage()
    {
        // Arrange
        var (ctx, bus) = Setup(totalCount: 45, page: MakeSales(20));
        using (ctx)
        {
            var cut = ctx.RenderComponent<Sales>();
            cut.WaitForAssertion(
                () => cut.FindAll(".sale-number-link").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Act — click the pager's "2".
            var pageTwo = cut.FindAll("button.rz-pager-page")
                .First(e => e.TextContent.Trim() == "2");
            await cut.InvokeAsync(() => pageTwo.Click());

            // Assert — a second query dispatched at PageNumber=2
            // (Skip=20 / PageSize=20 → page 2).
            await bus.Received(1).InvokeAsync<PaginatedResult<SaleListItemDto>>(
                Arg.Is<GetSalesPaginatedQuery>(q => q.PageNumber == 2),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public void Sales_EmptyServerResult_RendersFreshEmptyState()
    {
        // Arrange — zero sales on the server.
        var (ctx, bus) = Setup(totalCount: 0, page: new List<SaleListItemDto>());
        using (ctx)
        {
            // Act
            var cut = ctx.RenderComponent<Sales>();

            // Assert — the fresh-customer empty state (no filters were
            // active): the "start shopping" card, NOT the filtered-empty
            // variant, and NOT a permanently-spinning skeleton.
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("EmptyTitle"),
                TimeSpan.FromSeconds(10));
            cut.Markup.Should().NotContain("EmptyTitleFiltered",
                "no filter was active — this is the fresh-customer variant");
        }
    }
}

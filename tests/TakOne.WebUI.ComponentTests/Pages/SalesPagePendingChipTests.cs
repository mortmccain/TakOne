using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using TakOne.Application.Resources;
using TakOne.Application.Sales.DTOs;
using TakOne.Application.Sales.Queries.GetSalesPaginated;
using TakOne.Domain.Sales.Enums;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.Pages.Sales;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit tests for the Sales page's "Pending only"
/// quick-filter chip and the Dashboard's Pending-Approvals KPI deep link
/// into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. CLICKING the chip drives the Status column's filter through the
///      real Radzen descriptor pipeline — a follow-up dispatched
///      <see cref="GetSalesPaginatedQuery"/> carries
///      <c>Status = SaleStatus.Pending</c> and the chip flips to its
///      active state (aria-pressed + the active modifier class). Clicking
///      again CLEARS the filter (a later query with a null Status) and
///      deactivates the chip.
///   2. The DEEP LINK (?status=Pending — the URL the Dashboard's
///      Pending-Approvals KPI card builds) restores the same filter on
///      mount through the page's existing URL state contract, and the
///      chip reflects it (derived state, no bookkeeping).
/// </para>
/// <para>
/// <b>ASSERTION STYLE</b>: the fake bus RECORDS every dispatched query in
/// order; assertions inspect the SEQUENCE (contains-at-least / latest)
/// instead of NSubstitute's exact-count Received — Radzen may re-fire
/// LoadData internally when a column's FilterValue changes, and the
/// contract under test is "the filter reaches the query", not the exact
/// dispatch count. (Same discipline as
/// <see cref="AdminProductsLowStockChipTests"/>.)
/// </para>
/// <para>
/// <b>WIRING</b>: same harness as
/// <see cref="SalesPageLoadDataTests"/> — a fake bus returning a canned
/// page, the identity localizer, and bUnit's real NavigationManager
/// (pre-navigated for the deep-link case BEFORE the component mounts).
/// </para>
/// </remarks>
public class SalesPagePendingChipTests
{
    private static List<SaleListItemDto> MakeSales(int count) =>
        Enumerable.Range(1, count).Select(i => new SaleListItemDto
        {
            Id = Guid.NewGuid(),
            SaleNumber = $"INT-1405-{i:00000000}",
            CustomerName = $"Customer {i:000}",
            // Mixed statuses so the pending filter is meaningful in the
            // canned data (the fake bus ignores filters — the rows' own
            // statuses are irrelevant to the query-shape assertions).
            Status = i % 3 == 0 ? "Pending" : "Approved",
            Total = new MoneyDto { Amount = i * 100m, Currency = "IRR" },
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-i),
            CreatedByUserId = Guid.NewGuid(),
            CreatedByName = "Test Staff"
        }).ToList();

    private static (TestContext ctx, List<GetSalesPaginatedQuery> dispatched) Setup(
        int totalCount,
        List<SaleListItemDto> page)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer for the page's own Loc[...] lookups (the
        // chip's label + tooltip are plain keys — no format args).
        ComponentTestSetup.AddIdentityLocalizer<Sales>(ctx);

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

        // The fake bus: a canned page + a RECORD of every dispatched
        // query, in order (see the class remarks for why sequence-based
        // assertions beat exact counts here).
        var dispatched = new List<GetSalesPaginatedQuery>();
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<PaginatedResult<SaleListItemDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetSalesPaginatedQuery>().Subject;
                lock (dispatched)
                {
                    dispatched.Add(query);
                }
                return new PaginatedResult<SaleListItemDto>(page, totalCount, query.PageNumber, query.PageSize);
            });
        ctx.Services.AddSingleton(bus);

        return (ctx, dispatched);
    }

    /// <summary> Finds the toolbar's pending-only toggle chip button. </summary>
    private static IElement FindChip(IRenderedComponent<Sales> cut)
        => cut.Find("button.sales-pending-chip");

    [Fact]
    public async Task Sales_ChipClick_AppliesAndClearsPendingFilter()
    {
        // Arrange — a page of sales; total large enough to matter.
        var (ctx, dispatched) = Setup(totalCount: 45, page: MakeSales(20));
        using (ctx)
        {
            var cut = ctx.RenderComponent<Sales>();
            cut.WaitForAssertion(
                () => cut.FindAll(".sale-number-link").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Initially inactive: no status filter on the initial load.
            var chip = FindChip(cut);
            chip.GetAttribute("aria-pressed").Should().Be("false");
            chip.ClassList.Should().NotContain("sales-pending-chip--active");
            dispatched.Should().NotBeEmpty();
            dispatched.All(q => q.Status is null).Should().BeTrue(
                "the initial mount dispatches unfiltered queries only");

            // Act — click the chip ON.
            await cut.InvokeAsync(() => FindChip(cut).Click());

            // Assert — a follow-up query dispatched with the Pending
            // status filter, and the chip is active.
            cut.WaitForAssertion(
                () => dispatched.Any(q => q.Status == SaleStatus.Pending)
                    .Should().BeTrue("a follow-up query carried the pending filter"),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().Contain("sales-pending-chip--active");

            // Act — click the chip OFF again.
            await cut.InvokeAsync(() => FindChip(cut).Click());

            // Assert — the LATEST query carries NO status filter again,
            // and the chip returned to its inactive state.
            cut.WaitForAssertion(
                () => dispatched.Last().Status.Should().BeNull(),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("false"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().NotContain("sales-pending-chip--active");
        }
    }

    [Fact]
    public void Sales_PendingDeepLink_RestoresFilterAndActivatesChip()
    {
        // Arrange — the exact URL the Dashboard's Pending-Approvals KPI
        // card builds (PendingSalesDeepLink): pre-navigate BEFORE
        // mounting so the page's OnInitializedAsync URL parse sees it.
        var (ctx, dispatched) = Setup(totalCount: 45, page: MakeSales(20));
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/Sales?status=Pending");

            // Act — mount the page; the one-shot restore applies the
            // Status column filter, then the grid's (re)load dispatches.
            var cut = ctx.RenderComponent<Sales>();
            cut.WaitForAssertion(
                () => cut.FindAll(".sale-number-link").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Assert — a dispatched query carried the restored pending
            // filter (the restore path re-fires LoadData through the
            // same descriptor pipeline as the chip).
            cut.WaitForAssertion(
                () => dispatched.Any(q => q.Status == SaleStatus.Pending)
                    .Should().BeTrue("a dispatched query carried the restored pending filter"),
                TimeSpan.FromSeconds(5));

            // The chip — whose state DERIVES from the active filter —
            // renders active without any restore-specific bookkeeping.
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().Contain("sales-pending-chip--active");
        }
    }
}

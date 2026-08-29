using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Products.DTOs;
using TakOne.Application.Products.Queries.GetProductsPaginated;
using TakOne.Application.Resources;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.Pages.AdminProducts;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit test for the AdminProducts grid's Radzen LoadData
/// (server-driven paging) wiring — Round 6's riskiest glue. Mirrors
/// <see cref="AdminUsersPageLoadDataTests"/> from Round 5.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b> (that the per-layer tests cannot): the page
/// actually MOUNTS the grid with <c>Data=null</c> (the pinned Radzen
/// contract for LoadData-on-init), the grid's initial LoadData reaches
/// <c>IMessageBus.InvokeAsync</c> with a correctly-shaped query (page 1,
/// page size 20, IncludeInactive=true — the staff view), and the returned
/// page flows back into the grid via <c>Data</c> + <c>Count</c> (rows
/// render; the pager shows the server-side total).
/// </para>
/// <para>
/// <b>WIRING</b>: the IMessageBus is an NSubstitute fake returning a
/// canned page; every other dependency uses the same real/stub mix the
/// rest of this project's component tests use. Unlike AdminUsers, this
/// page resolves no roles and loads no lookup lists in
/// OnInitializedAsync (the category-name filters resolve server-side), so
/// no auth-state or extra-query setup is needed — the products query is
/// the ONLY message the mount dispatches.
/// </para>
/// </remarks>
public class AdminProductsPageLoadDataTests
{
    private static List<ProductListItemDto> MakeProducts(int count) =>
        Enumerable.Range(1, count).Select(i => new ProductListItemDto
        {
            Id = Guid.NewGuid(),
            Name = $"Product {i:000}",
            Description = string.Empty,
            PictureUrl = null,
            Price = new MoneyDto { Amount = 1000m + i, Currency = "IRR" },
            StockQuantity = i % 5, // a mix of in-stock and out-of-stock rows
            CategoryId = Guid.NewGuid(),
            SubCategoryId = null,
            SubSubCategoryId = null,
            CategoryName = "Test Category",
            CategoryIsActive = true,
            SubCategoryName = string.Empty,
            SubCategoryIsActive = true,
            SubSubCategoryName = string.Empty,
            SubSubCategoryIsActive = true,
            MyPurchaseLimit = null
        }).ToList();

    private static (TestContext ctx, IMessageBus bus) Setup(int totalCount, List<ProductListItemDto> page)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer for the page's own Loc[...] lookups.
        ComponentTestSetup.AddIdentityLocalizer<AdminProducts>(ctx);

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

        // AdminProducts injects ILogger<AdminProducts> (dialog-open failure
        // paths).
        ctx.Services.AddSingleton(Substitute.For<ILogger<AdminProducts>>());

        // AdminProducts injects Radzen.DialogService (restock/deactivate
        // confirms) — the spy substitute covers the injection without
        // opening dialogs.
        ComponentTestSetup.AddDialogServiceSpy(ctx);

        // The fake bus: a canned products page. This is the ONLY message
        // the mount dispatches (the grid's LoadData-on-init).
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<PaginatedResult<ProductListItemDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetProductsPaginatedQuery>().Subject;
                return new PaginatedResult<ProductListItemDto>(page, totalCount, query.PageNumber, query.PageSize);
            });
        ctx.Services.AddSingleton(bus);

        return (ctx, bus);
    }

    [Fact]
    public async Task AdminProducts_Mount_FiresLoadData_AndRendersPageWithServerTotal()
    {
        // Arrange — 45 "server" rows, page 1 of 20.
        var (ctx, bus) = Setup(totalCount: 45, page: MakeProducts(20));
        using (ctx)
        {
            // Act — mount the page. The grid's LoadData-on-init fires
            // during the initial render lifecycle.
            var cut = ctx.RenderComponent<AdminProducts>();

            // Assert — the load settles: 20 rows render (every row carries
            // a .stock-cell span), and the pager reflects the SERVER total
            // (45), not the local page size.
            cut.WaitForAssertion(
                () => cut.FindAll(".stock-cell").Should().HaveCount(20),
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

            // The dispatched query was the initial page: page 1, size 20,
            // IncludeInactive=true (the staff view), and the default
            // Name-ascending sort (null SortBy + not descending).
            await bus.Received(1).InvokeAsync<PaginatedResult<ProductListItemDto>>(
                Arg.Is<GetProductsPaginatedQuery>(q =>
                    q.PageNumber == 1 &&
                    q.PageSize == 20 &&
                    q.IncludeInactive &&
                    q.SortBy == null &&
                    !q.SortDescending),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task AdminProducts_PagerPageTwoClick_DispatchesSecondPage()
    {
        // Arrange
        var (ctx, bus) = Setup(totalCount: 45, page: MakeProducts(20));
        using (ctx)
        {
            var cut = ctx.RenderComponent<AdminProducts>();
            cut.WaitForAssertion(
                () => cut.FindAll(".stock-cell").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Act — click the pager's "2".
            var pageTwo = cut.FindAll("button.rz-pager-page")
                .First(e => e.TextContent.Trim() == "2");
            await cut.InvokeAsync(() => pageTwo.Click());

            // Assert — a second query dispatched at PageNumber=2
            // (Skip=20 / PageSize=20 → page 2).
            await bus.Received(1).InvokeAsync<PaginatedResult<ProductListItemDto>>(
                Arg.Is<GetProductsPaginatedQuery>(q => q.PageNumber == 2),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task AdminProducts_SortHeaderClick_DispatchesTranslatedSort()
    {
        // Arrange — the Stock column's header click must travel through
        // the translator into the typed sort key.
        var (ctx, bus) = Setup(totalCount: 45, page: MakeProducts(20));
        using (ctx)
        {
            var cut = ctx.RenderComponent<AdminProducts>();
            cut.WaitForAssertion(
                () => cut.FindAll(".stock-cell").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Act — click the Stock column's header title (Radzen binds
            // the sort toggle to the title div's onclick).
            var headers = cut.FindAll("th > div");
            var stockHeader = headers.First(h =>
                h.TextContent.Contains("ColStock", StringComparison.Ordinal));
            await cut.InvokeAsync(() => stockHeader.Click());

            // Assert — a query dispatched with the translated stock sort
            // (ascending on the first click).
            await bus.Received(1).InvokeAsync<PaginatedResult<ProductListItemDto>>(
                Arg.Is<GetProductsPaginatedQuery>(q =>
                    q.SortBy == ProductSortBy.StockLowToHigh &&
                    !q.SortDescending),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public void AdminProducts_EmptyServerResult_RendersFreshEmptyState()
    {
        // Arrange — zero products on the server (fresh install).
        var (ctx, bus) = Setup(totalCount: 0, page: new List<ProductListItemDto>());
        using (ctx)
        {
            // Act
            var cut = ctx.RenderComponent<AdminProducts>();

            // Assert — the fresh-install empty state (no filters were
            // active): the "no products yet" card, NOT the filtered-empty
            // variant, and NOT a permanently-spinning skeleton.
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("EmptyTitle"),
                TimeSpan.FromSeconds(10));
            cut.Markup.Should().NotContain("EmptyTitleFiltered",
                "no filter was active — this is the fresh-install variant");
        }
    }
}

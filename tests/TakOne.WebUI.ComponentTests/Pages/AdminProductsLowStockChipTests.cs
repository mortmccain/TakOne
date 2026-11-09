using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
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
/// Page-level bUnit tests for the AdminProducts "Low stock only"
/// quick-filter chip and the Dashboard's restock-banner deep link into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. CLICKING the chip drives the Stock column's numeric filter through
///      the real Radzen descriptor pipeline — a follow-up dispatched
///      <see cref="GetProductsPaginatedQuery"/> carries
///      <c>StockFilter = (LessThanOrEqual, threshold)</c> and the chip
///      flips to its active state (aria-pressed + the active modifier
///      class). Clicking again CLEARS the filter (a later query with a
///      null StockFilter) and deactivates the chip.
///   2. The DEEP LINK (?stockn=10&amp;stocknop=LessThanOrEqual — the URL the
///      Dashboard's restock banner builds) restores the same filter on
///      mount through the page's existing URL state contract, and the
///      chip reflects it (derived state, no bookkeeping).
/// </para>
/// <para>
/// <b>ASSERTION STYLE</b>: the fake bus RECORDS every dispatched query in
/// order; assertions inspect the SEQUENCE (contains-at-least / latest)
/// instead of NSubstitute's exact-count Received — Radzen may re-fire
/// LoadData internally when a column's FilterValue changes, and the
/// contract under test is "the filter reaches the query", not the exact
/// dispatch count.
/// </para>
/// <para>
/// <b>WIRING</b>: same harness as
/// <see cref="AdminProductsPageLoadDataTests"/> — a fake bus returning a
/// canned page, the identity localizer, and bUnit's real NavigationManager
/// (pre-navigated for the deep-link case BEFORE the component mounts).
/// </para>
/// </remarks>
public class AdminProductsLowStockChipTests
{
    private static List<ProductListItemDto> MakeProducts(int count) =>
        Enumerable.Range(1, count).Select(i => new ProductListItemDto
        {
            Id = Guid.NewGuid(),
            Name = $"Product {i:000}",
            Description = string.Empty,
            PictureUrl = null,
            Price = new MoneyDto { Amount = 1000m + i, Currency = "IRR" },
            StockQuantity = i % 5,
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

    private static (BunitContext ctx, List<GetProductsPaginatedQuery> dispatched) Setup(
        int totalCount,
        List<ProductListItemDto> page)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Local twin of AddIdentityLocalizer that ALSO stubs the
        // format-args indexer (the chip's tooltip formats the threshold).
        var loc = Substitute.For<IStringLocalizer<AdminProducts>>();
        loc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        loc[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(loc);

        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));
        ctx.Services.AddSingleton(Substitute.For<ILogger<AdminProducts>>());
        ComponentTestSetup.AddDialogServiceSpy(ctx);

        // The fake bus: a canned page + a RECORD of every dispatched
        // query, in order (see the class remarks for why sequence-based
        // assertions beat exact counts here).
        var dispatched = new List<GetProductsPaginatedQuery>();
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<PaginatedResult<ProductListItemDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetProductsPaginatedQuery>().Subject;
                lock (dispatched)
                {
                    dispatched.Add(query);
                }
                return new PaginatedResult<ProductListItemDto>(page, totalCount, query.PageNumber, query.PageSize);
            });
        ctx.Services.AddSingleton(bus);

        return (ctx, dispatched);
    }

    /// <summary> Finds the header's low-stock toggle chip button. </summary>
    private static IElement FindChip(IRenderedComponent<AdminProducts> cut)
        => cut.Find("button.low-stock-chip");

    [Fact]
    public async Task AdminProducts_ChipClick_AppliesAndClearsLowStockFilter()
    {
        // Arrange — a page of products; total large enough to matter.
        var (ctx, dispatched) = Setup(totalCount: 45, page: MakeProducts(20));
        using (ctx)
        {
            var cut = ctx.Render<AdminProducts>();
            cut.WaitForAssertion(
                () => cut.FindAll(".stock-cell").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Initially inactive: no stock filter on the initial load.
            var chip = FindChip(cut);
            chip.GetAttribute("aria-pressed").Should().Be("false");
            chip.ClassList.Should().NotContain("low-stock-chip--active");
            dispatched.Should().NotBeEmpty();
            dispatched.All(q => q.StockFilter is null).Should().BeTrue(
                "the initial mount dispatches unfiltered queries only");

            // Act — click the chip ON.
            await cut.InvokeAsync(() => FindChip(cut).Click());

            // Assert — a follow-up query dispatched with the low-stock
            // filter (≤ the shared threshold), and the chip is active.
            cut.WaitForAssertion(
                () => dispatched.Any(q =>
                    q.StockFilter is { Operator: ProductsNumberOperator.LessThanOrEqual, Value: 10 })
                    .Should().BeTrue("a follow-up query carried the low-stock filter"),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().Contain("low-stock-chip--active");

            // Act — click the chip OFF again.
            await cut.InvokeAsync(() => FindChip(cut).Click());

            // Assert — the LATEST query carries NO stock filter again, and
            // the chip returned to its inactive state.
            cut.WaitForAssertion(
                () => dispatched.Last().StockFilter.Should().BeNull(),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("false"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().NotContain("low-stock-chip--active");
        }
    }

    [Fact]
    public void AdminProducts_LowStockDeepLink_RestoresFilterAndActivatesChip()
    {
        // Arrange — the exact URL the Dashboard's restock banner builds
        // (LowStockDeepLink): pre-navigate BEFORE mounting so the page's
        // OnInitializedAsync URL parse sees it.
        var (ctx, dispatched) = Setup(totalCount: 45, page: MakeProducts(20));
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/Admin/Products?stockn=10&stocknop=LessThanOrEqual");

            // Act — mount the page; the one-shot restore applies the
            // Stock column filter, then the grid's (re)load dispatches.
            var cut = ctx.Render<AdminProducts>();
            cut.WaitForAssertion(
                () => cut.FindAll(".stock-cell").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Assert — a dispatched query carried the restored low-stock
            // filter (the restore path re-fires LoadData through the
            // same descriptor pipeline as the chip).
            cut.WaitForAssertion(
                () => dispatched.Any(q =>
                    q.StockFilter is { Operator: ProductsNumberOperator.LessThanOrEqual, Value: 10 })
                    .Should().BeTrue("a dispatched query carried the restored low-stock filter"),
                TimeSpan.FromSeconds(5));

            // The chip — whose state DERIVES from the active filter —
            // renders active without any restore-specific bookkeeping.
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().Contain("low-stock-chip--active");
        }
    }
}

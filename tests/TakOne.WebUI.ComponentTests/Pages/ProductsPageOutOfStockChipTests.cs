using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using TakOne.Application.Categories.DTOs;
using TakOne.Application.Categories.Queries.GetActiveCategories;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Common.Errors;
using TakOne.Application.Products.DTOs;
using TakOne.Application.Products.Queries.GetProductsPaginated;
using TakOne.Application.Resources;
using TakOne.Application.Sales.DTOs;
using TakOne.Application.Sales.Queries.GetActiveCartForUser;
using TakOne.Application.Sales.Queries.GetCustomerShopStats;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.Pages.Products;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Shared bUnit harness for mounting the FULL shop Products page
/// (QA-10 round — used by <see cref="ProductsPageOutOfStockChipTests"/>
/// and <see cref="ProductsPagePageSizeTests"/>).
/// </summary>
/// <remarks>
/// <para>
/// The shop page dispatches FOUR query types through the message bus on
/// init (categories, products, cart, stats) — the fake bus answers each
/// with a canned minimal success so the full page renders. Roles are
/// wired twice, matching production: the bUnit authorization pipeline
/// (for the page's <c>AuthorizeView</c>) AND an
/// <see cref="ICurrentUserService"/> substitute (the page resolves its
/// role gates through <c>CurrentUser.IsInRole</c>).
/// </para>
/// <para>
/// The products queries are RECORDED in order (the recording fake bus)
/// — assertions inspect the SEQUENCE rather than exact dispatch
/// counts, the same discipline as <see cref="SalesPagePendingChipTests"/>
/// and <see cref="AdminProductsLowStockChipTests"/>.
/// </para>
/// </remarks>
internal static class ProductsPageTestHarness
{
    /// <summary>
    /// Mounts the full shop page with the supplied role set. Returns the
    /// test context and the ordered list of every dispatched
    /// <see cref="GetProductsPaginatedQuery"/>.
    /// </summary>
    internal static (BunitContext ctx, List<GetProductsPaginatedQuery> dispatched) Setup(
        string[] roles,
        int totalCount,
        List<ProductListItemDto> page)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer for the page's own Loc[...] lookups (the
        // chips' labels + tooltips are plain keys — no format args).
        ComponentTestSetup.AddIdentityLocalizer<Products>(ctx);

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

        // Full bUnit authorization pipeline (the page renders an
        // AuthorizeView around the hero cart button).
        ComponentTestSetup.AddBunitAuthorizedUser(ctx, "tester", roles);

        // The page's own role gates run through ICurrentUserService —
        // mirror the same role set so the chip's visibility matches what
        // production would compute for this user.
        var userId = Guid.NewGuid();
        var roleSet = roles.ToHashSet(StringComparer.Ordinal);
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(userId);
        currentUser.WorkerId.Returns("10001");
        currentUser.FullName.Returns("Test User");
        currentUser.IsInRole(Arg.Any<string>())
            .Returns(ci => roleSet.Contains((string)ci[0]!));
        ctx.Services.AddSingleton(currentUser);

        // The fake bus: canned answers for the four query types the page
        // dispatches on init, plus a RECORD of every products query, in
        // order (see the class remarks for why sequence-based assertions
        // beat exact counts here).
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

        bus.InvokeAsync<Result<List<CategoryDto>>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<CategoryDto>>.Success(new List<CategoryDto>()));

        bus.InvokeAsync<Result<CartDto?>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<CartDto?>.Success(null));

        bus.InvokeAsync<Result<CustomerShopStatsDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<CustomerShopStatsDto>.Success(new CustomerShopStatsDto()));

        ctx.Services.AddSingleton(bus);

        return (ctx, dispatched);
    }
}

/// <summary>
/// Page-level bUnit tests for the shop Products page's "Include out
/// of stock" toggle chip (QA-10 — the Admin/Manager view-mode chip
/// in the products tools row).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. The chip renders ONLY for Admin/Manager (the roles
///      <c>GetProductsPaginatedQueryHandler</c> honors for
///      <c>IncludeInactive</c>) — a Customer never sees it, and a
///      hand-crafted <c>?oos=1</c> URL does not flip the customer's
///      local flag (matching the server-side clamp).
///   2. CLICKING the chip as an Admin drives the real page reload: a
///      follow-up dispatched <see cref="GetProductsPaginatedQuery"/>
///      carries <c>IncludeInactive = true</c>, the chip flips to its
///      active state (aria-pressed + the active modifier class), the
///      URL mirror gains <c>oos=1</c>, and zero-stock cards render the
///      sold-out card treatment. Clicking again CLEARS all of it.
///   3. The DEEP LINK (<c>?oos=1</c> — the URL the chip itself
///      persists) restores the view mode on mount through the page's
///      existing URL state contract, and the chip derives its active
///      state from it with zero restore-specific bookkeeping.
/// </para>
/// <para>
/// <b>WIRING</b>: see <see cref="ProductsPageTestHarness"/> (the
/// shared full-page mount). Assertion style: sequence-based (the
/// recording fake bus) — see the harness remarks.
/// </para>
/// </remarks>
public class ProductsPageOutOfStockChipTests
{
    private static List<ProductListItemDto> MakeProducts(int count, int zeroStockCount = 0) =>
        Enumerable.Range(1, count).Select(i => new ProductListItemDto
        {
            Id = Guid.NewGuid(),
            Name = $"Product {i:000}",
            Description = $"Description {i}",
            // The FIRST zeroStockCount products are out of stock — the
            // canned page mixes both so the sold-out card treatment is
            // assertable alongside the in-stock default.
            StockQuantity = i <= zeroStockCount ? 0 : 15 + i,
            Price = new MoneyDto { Amount = i * 10_000m, Currency = "IRR" }
        }).ToList();

    private static (BunitContext ctx, List<GetProductsPaginatedQuery> dispatched) Setup(
        string[] roles,
        int totalCount,
        List<ProductListItemDto> page)
        => ProductsPageTestHarness.Setup(roles, totalCount, page);

    /// <summary> Finds the tools row's out-of-stock toggle chip button. </summary>
    private static IElement FindChip(IRenderedComponent<Products> cut)
        => cut.Find("button.tm-oos-chip");

    [Fact]
    public async Task Products_ChipClick_TogglesIncludeInactiveAndPersistsUrl()
    {
        // Arrange — an admin viewing a full page (12 = the shop grid's
        // page size) with 2 zero-stock cards among them.
        var (ctx, dispatched) = Setup(
            new[] { Roles.Admin }, totalCount: 40, page: MakeProducts(12, zeroStockCount: 2));
        using (ctx)
        {
            var cut = ctx.Render<Products>();
            cut.WaitForAssertion(
                () => cut.FindAll(".tm-product-card").Should().HaveCount(12),
                TimeSpan.FromSeconds(10));

            // Initially inactive: the mount dispatched in-stock-only
            // queries, the chip is off, and the URL carries no oos flag.
            var chip = FindChip(cut);
            chip.GetAttribute("aria-pressed").Should().Be("false");
            chip.ClassList.Should().NotContain("tm-oos-chip--active");
            dispatched.Should().NotBeEmpty();
            dispatched.All(q => !q.IncludeInactive).Should().BeTrue(
                "the initial mount dispatches in-stock-only queries");

            // Act — click the chip ON.
            await cut.InvokeAsync(() => FindChip(cut).Click());

            // Assert — a follow-up query dispatched with the flag, the
            // chip is active, and the URL mirror picked up oos=1 (the
            // same URL contract the deep-link test restores from).
            cut.WaitForAssertion(
                () => dispatched.Any(q => q.IncludeInactive)
                    .Should().BeTrue("a follow-up query carried the out-of-stock flag"),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().Contain("tm-oos-chip--active");
            cut.WaitForAssertion(
                () => ctx.Services.GetRequiredService<NavigationManager>().Uri
                    .Should().Contain("oos=1", "the view mode persists in the URL for refresh survival"),
                TimeSpan.FromSeconds(5));

            // The sold-out card treatment renders on the zero-stock
            // cards (and ONLY those — the in-stock cards stay normal).
            cut.FindAll(".tm-product-card--out").Should().HaveCount(2);
            cut.FindAll(".tm-product-card").Should().HaveCount(12);

            // Act — click the chip OFF again.
            await cut.InvokeAsync(() => FindChip(cut).Click());

            // Assert — the LATEST query carries no flag, the chip
            // returned to its inactive state, and the URL mirror
            // dropped the oos parameter (non-default state only).
            cut.WaitForAssertion(
                () => dispatched.Last().IncludeInactive.Should().BeFalse(),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("false"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().NotContain("tm-oos-chip--active");
            cut.WaitForAssertion(
                () => ctx.Services.GetRequiredService<NavigationManager>().Uri
                    .Should().NotContain("oos=", "the off state cleans the URL flag"),
                TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Products_OosDeepLink_RestoresViewModeAndActivatesChip()
    {
        // Arrange — the exact URL the chip itself persists: pre-navigate
        // BEFORE mounting so the page's OnInitializedAsync URL parse
        // sees it. Includes one zero-stock card in the canned page —
        // exactly what the handler WOULD return with the flag on.
        var (ctx, dispatched) = Setup(
            new[] { Roles.Manager }, totalCount: 40, page: MakeProducts(12, zeroStockCount: 1));
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/Products?oos=1");

            // Act — mount the page; the restore applies the view mode
            // BEFORE the first products load, so the very first query
            // already carries the flag.
            var cut = ctx.Render<Products>();
            cut.WaitForAssertion(
                () => cut.FindAll(".tm-product-card").Should().HaveCount(12),
                TimeSpan.FromSeconds(10));

            cut.WaitForAssertion(
                () => dispatched.Should().NotBeEmpty(),
                TimeSpan.FromSeconds(5));
            dispatched[0].IncludeInactive.Should().BeTrue(
                "the FIRST products query already carried the restored view mode");

            // The chip — whose state is the same field the restore set —
            // renders active without any restore-specific bookkeeping.
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().Contain("tm-oos-chip--active");

            // The sold-out card treatment marks the zero-stock card.
            cut.FindAll(".tm-product-card--out").Should().HaveCount(1);
        }
    }

    [Fact]
    public void Products_ChipHiddenForCustomerAndOosUrlIgnored()
    {
        // Arrange — a Customer opening a STAFF-SHARED ?oos=1 link: the
        // chip must not render at all, and the local flag must stay off
        // (the server-side clamp in the handler makes the ignored flag
        // harmless — this pins the UI-side half of that contract).
        var (ctx, dispatched) = Setup(
            new[] { Roles.Customer }, totalCount: 40, page: MakeProducts(12));
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/Products?oos=1");

            var cut = ctx.Render<Products>();
            cut.WaitForAssertion(
                () => cut.FindAll(".tm-product-card").Should().HaveCount(12),
                TimeSpan.FromSeconds(10));

            // No chip in the markup — the tools row has only the sort
            // control and the export button for non-staff users.
            cut.FindAll("button.tm-oos-chip").Should().BeEmpty();

            // And the URL's flag never reached a query.
            cut.WaitForAssertion(
                () => dispatched.Should().NotBeEmpty(),
                TimeSpan.FromSeconds(5));
            dispatched.All(q => !q.IncludeInactive).Should().BeTrue(
                "a non-staff user's ?oos=1 link is ignored locally, mirroring the handler's server-side clamp");
        }
    }
}

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
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.MobilePages.MobileProducts;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Shared bUnit harness for mounting the FULL mobile shop page
/// (QA-11 round — the first page-level coverage MobileProducts has
/// ever had).
/// </summary>
/// <remarks>
/// <para>
/// The mobile page dispatches THREE query types through the message bus
/// on init (products, categories, cart) — the fake bus answers each
/// with a canned minimal success so the full page renders. Roles are
/// wired twice, matching production: the bUnit authorization pipeline
/// (the page carries <c>[Authorize]</c>) AND an
/// <see cref="ICurrentUserService"/> substitute (the page resolves its
/// role gates — read-only check, staff chip visibility — through
/// <c>CurrentUser.IsInRole</c>).
/// </para>
/// <para>
/// The products queries are RECORDED in order (the recording fake bus)
/// and answered through a per-page provider, so append-pagination
/// assertions can distinguish page 1 from page 2 payloads — the same
/// sequence-based discipline as <see cref="ProductsPageTestHarness"/>.
/// </para>
/// <para>
/// The localizer stub ALSO configures the format-args indexer (the
/// count line uses <c>Loc["ShowingCount", a, b, c]</c>); args are
/// rendered as <c>arg0|arg1|…</c> so formatted output is assertable.
/// </para>
/// </remarks>
internal static class MobileProductsPageTestHarness
{
    /// <summary>
    /// Mounts the full mobile shop page with the supplied role set.
    /// Returns the test context, the ordered list of every dispatched
    /// <see cref="GetProductsPaginatedQuery"/>, and the bUnit JS interop
    /// (loose mode — the CSV export's takDownload.csv invocations land
    /// in the global invocation dictionary).
    /// </summary>
    /// <param name="roles">The caller's role set (production wiring is
    /// mirrored into BOTH the bUnit auth pipeline and the
    /// <see cref="ICurrentUserService"/> substitute).</param>
    /// <param name="totalCount">The canned TotalCount for every
    /// products page (drives the load-more button + count line).</param>
    /// <param name="pageProvider">Answers each products query with the
    /// payload for ITS page number — defaults to a fixed single page
    /// (the export test's shape: every page = the same short list,
    /// which makes the export loop terminate after one fetch).</param>
    internal static (TestContext ctx, List<GetProductsPaginatedQuery> dispatched, BunitJSInterop js) Setup(
        string[] roles,
        int totalCount,
        Func<GetProductsPaginatedQuery, List<ProductListItemDto>>? pageProvider = null)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer for the page's own Loc[...] lookups, with
        // the format-args twin (renders args as a|b|c — see remarks).
        var loc = Substitute.For<IStringLocalizer<MobileProducts>>();
        loc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        loc[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
        {
            var args = ci.ArgAt<object[]>(1) ?? Array.Empty<object>();
            return new LocalizedString(
                (string)ci.ArgAt<string>(0),
                string.Join("|", args),
                false);
        });
        ctx.Services.AddSingleton(loc);

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

        // The page carries [Authorize] — the full bUnit auth pipeline.
        ComponentTestSetup.AddBunitAuthorizedUser(ctx, "tester", roles);

        // The page's own role gates run through ICurrentUserService —
        // mirror the same role set so the chip's visibility matches what
        // production would compute for this user.
        var roleSet = roles.ToHashSet(StringComparer.Ordinal);
        var currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsAuthenticated.Returns(true);
        currentUser.UserId.Returns(Guid.NewGuid());
        currentUser.WorkerId.Returns("10001");
        currentUser.FullName.Returns("Test User");
        currentUser.IsInRole(Arg.Any<string>())
            .Returns(ci => roleSet.Contains((string)ci[0]!));
        ctx.Services.AddSingleton(currentUser);

        // The fake bus: canned answers for the three query types the
        // page dispatches on init, plus a RECORD of every products
        // query, in order. The per-page provider lets append-pagination
        // tests hand out DISTINCT payloads per page.
        var provider = pageProvider ?? (_ => MakeProducts(10));
        var dispatched = new List<GetProductsPaginatedQuery>();
        var bus = Substitute.For<IMessageBus>();

        bus.InvokeAsync<PaginatedResult<ProductListItemDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetProductsPaginatedQuery>().Subject;
                List<ProductListItemDto> page;
                lock (dispatched)
                {
                    dispatched.Add(query);
                    page = provider(query);
                }
                return new PaginatedResult<ProductListItemDto>(page, totalCount, query.PageNumber, query.PageSize);
            });

        bus.InvokeAsync<Result<List<CategoryDto>>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<CategoryDto>>.Success(new List<CategoryDto>()));

        bus.InvokeAsync<Result<CartDto?>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<CartDto?>.Success(null));

        ctx.Services.AddSingleton(bus);

        return (ctx, dispatched, ctx.JSInterop);
    }

    /// <summary>
    /// Makes <paramref name="count"/> distinct products (offset lets
    /// page 2+ payloads carry different names/ids than page 1).
    /// The first <paramref name="zeroStockCount"/> rows are out of
    /// stock — the canned page mixes both so the sold-out card
    /// treatment is assertable alongside the in-stock default.
    /// </summary>
    internal static List<ProductListItemDto> MakeProducts(int count, int offset = 0, int zeroStockCount = 0) =>
        Enumerable.Range(1, count).Select(i =>
        {
            var n = offset + i;
            return new ProductListItemDto
            {
                Id = Guid.NewGuid(),
                Name = $"Product {n:000}",
                Description = $"Description {n}",
                StockQuantity = i <= zeroStockCount ? 0 : 15 + i,
                Price = new MoneyDto { Amount = n * 10_000m, Currency = "IRR" }
            };
        }).ToList();
}

/// <summary>
/// Page-level bUnit tests for the QA-11 mobile shop parity round:
/// append-style "load more" pagination, the staff "include out of
/// stock" chip (oos=1 URL contract + sold-out card treatment), and
/// the customer-safe catalog CSV export.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. PAGINATION: the list loads 24 rows per page (the old page
///      fetched one silent 50-row page with no hint more existed);
///      "Load more" appends the NEXT page as a follow-up query; the
///      count line tracks the loaded/total split; the button hides at
///      the end; sort changes restart at page 1 with a FRESH list.
///   2. STAFF VIEW MODE: the chip renders ONLY for Admin/Manager; the
///      role gate is UX-only (the handler clamps IncludeInactive
///      server-side) — a Customer never sees the chip, and a
///      hand-crafted <c>?oos=1</c> URL does not flip the customer's
///      local flag. Clicking as Admin drives the real reload: a
///      follow-up query carries <c>IncludeInactive = true</c>, the
///      chip flips active (aria-pressed + modifier class), the URL
///      mirror gains <c>oos=1</c>, and zero-stock cards render the
///      sold-out treatment.
///   3. EXPORT: the download re-issues the SAME query (filters + sort
///      + view mode) and produces the customer-safe 7-column CSV
///      (name, category path, price, currency, availability label)
///      via the shared takDownload.csv pipeline.
/// </para>
/// <para>
/// <b>WIRING</b>: see <see cref="MobileProductsPageTestHarness"/>.
/// Assertion style: sequence-based (the recording fake bus) — re-Find
/// elements before each event dispatch (Blazor re-renders after every
/// dispatch; captured references go stale).
/// </para>
/// </remarks>
public class MobileProductsParityTests
{
    // The mobile page's per-page row count (mirrors the page const).
    private const int MobilePageSize = 24;

    private static (TestContext ctx, List<GetProductsPaginatedQuery> dispatched, BunitJSInterop js) Setup(
        string[] roles,
        int totalCount,
        Func<GetProductsPaginatedQuery, List<ProductListItemDto>>? pageProvider = null)
        => MobileProductsPageTestHarness.Setup(roles, totalCount, pageProvider);

    /// <summary> Renders and waits for the initial product cards. </summary>
    private static IRenderedComponent<MobileProducts> Mount(TestContext ctx, int expectedCards)
    {
        var cut = ctx.RenderComponent<MobileProducts>();
        cut.WaitForAssertion(
            () => cut.FindAll(".m-product-card").Should().HaveCount(expectedCards),
            TimeSpan.FromSeconds(10));
        return cut;
    }

    /// <summary> Finds the toolbar's out-of-stock toggle chip button. </summary>
    private static IElement FindChip(IRenderedComponent<MobileProducts> cut)
        => cut.Find("button.m-oos-chip");

    // ── Pagination ──────────────────────────────────────────────────

    [Fact]
    public async Task MobileProducts_LoadMore_AppendsNextPageAndHidesAtEnd()
    {
        // Arrange — a 60-product catalog, 24 per page; the provider
        // hands out DISTINCT payloads per page (offset by page number)
        // so appension is observable in the rendered card count.
        const int total = 60;
        var (ctx, dispatched, _) = Setup(
            new[] { Roles.Customer },
            totalCount: total,
            pageProvider: q =>
            {
                var already = (q.PageNumber - 1) * MobilePageSize;
                return MobileProductsPageTestHarness.MakeProducts(
                    Math.Min(MobilePageSize, total - already), offset: already);
            });
        using (ctx)
        {
            var cut = Mount(ctx, MobilePageSize);

            // The initial mount asked for page 1 of 24 (the new contract
            // — the old page silently fetched one 50-row page).
            dispatched.Should().HaveCountGreaterThanOrEqualTo(1);
            dispatched[0].PageNumber.Should().Be(1);
            dispatched[0].PageSize.Should().Be(MobilePageSize);

            // Count line reflects the loaded/total split (the format-args
            // stub renders args as a|b|c: first, last, total).
            cut.Find(".m-products-count").TextContent.Trim().Should().Be($"1|{MobilePageSize}|{total}");

            // Act — Load more: appends page 2.
            await cut.InvokeAsync(() => cut.Find("button.m-load-more-btn").Click());

            // Assert — a follow-up query for page 2 (24 more rows), the
            // list APPENDED (48 cards), and the count line advanced.
            cut.WaitForAssertion(
                () => cut.FindAll(".m-product-card").Should().HaveCount(MobilePageSize * 2),
                TimeSpan.FromSeconds(5));
            dispatched.Last().PageNumber.Should().Be(2);
            dispatched.Last().PageSize.Should().Be(MobilePageSize);
            cut.Find(".m-products-count").TextContent.Trim().Should().Be($"1|{MobilePageSize * 2}|{total}");

            // Act again — the final partial page (12 more rows).
            await cut.InvokeAsync(() => cut.Find("button.m-load-more-btn").Click());

            // Assert — everything loaded: the button disappears (no more
            // pages) and the count line reads the full catalog.
            cut.WaitForAssertion(
                () => cut.FindAll(".m-product-card").Should().HaveCount(total),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => cut.FindAll("button.m-load-more-btn").Should().BeEmpty(
                    "the button hides once every row is loaded"),
                TimeSpan.FromSeconds(5));
            cut.Find(".m-products-count").TextContent.Trim().Should().Be($"1|{total}|{total}");
        }
    }

    [Fact]
    public void MobileProducts_SinglePageCatalog_HidesLoadMoreButKeepsCount()
    {
        // Arrange — a catalog that fits one page (10 of 24).
        var (ctx, _, _) = Setup(
            new[] { Roles.Customer }, totalCount: 10,
            pageProvider: _ => MobileProductsPageTestHarness.MakeProducts(10));
        using (ctx)
        {
            var cut = Mount(ctx, 10);

            // No load-more noise on a single-page catalog…
            cut.FindAll("button.m-load-more-btn").Should().BeEmpty();

            // …but the count line still tells the shopper the catalog
            // size (informative, not actionable).
            cut.Find(".m-products-count").TextContent.Trim().Should().Be("1|10|10");
        }
    }

    [Fact]
    public async Task MobileProducts_SortChange_RestartsAtPage1WithFreshList()
    {
        // Arrange — a 60-product catalog; only page 1 loaded so far.
        const int total = 60;
        var (ctx, dispatched, _) = Setup(
            new[] { Roles.Customer },
            totalCount: total,
            pageProvider: q =>
            {
                var already = (q.PageNumber - 1) * MobilePageSize;
                return MobileProductsPageTestHarness.MakeProducts(
                    Math.Min(MobilePageSize, total - already), offset: already);
            });
        using (ctx)
        {
            var cut = Mount(ctx, MobilePageSize);

            // Act — pick a different sort: the whole result set
            // re-orders, so paging must restart at page 1 with a FRESH
            // list (never an append).
            await cut.InvokeAsync(() => cut.Find("#m-products-sort")
                .Change(nameof(ProductSortBy.PriceLowToHigh)));

            // Assert — the follow-up query restarted at page 1 carrying
            // the new sort, and the list was REPLACED (still one page
            // of cards — the old page-2 rows never joined).
            cut.WaitForAssertion(
                () => dispatched.Last().PageNumber.Should().Be(1,
                    "a sort change re-orders the whole result set: page 1, fresh"),
                TimeSpan.FromSeconds(5));
            dispatched.Last().SortBy.Should().Be(ProductSortBy.PriceLowToHigh);
            cut.WaitForAssertion(
                () => cut.FindAll(".m-product-card").Should().HaveCount(MobilePageSize),
                TimeSpan.FromSeconds(5));
        }
    }

    // ── Staff view mode (the QA-10 desktop chip, ported) ────────────

    [Fact]
    public async Task MobileProducts_ChipClick_TogglesIncludeInactiveAndPersistsUrl()
    {
        // Arrange — an admin viewing a full page (24) with 2 zero-stock
        // cards among them.
        var (ctx, dispatched, _) = Setup(
            new[] { Roles.Admin }, totalCount: 40,
            pageProvider: _ => MobileProductsPageTestHarness.MakeProducts(MobilePageSize, zeroStockCount: 2));
        using (ctx)
        {
            var cut = Mount(ctx, MobilePageSize);

            // Initially inactive: the mount dispatched in-stock-only
            // queries, the chip is off, and the URL carries no oos flag.
            var chip = FindChip(cut);
            chip.GetAttribute("aria-pressed").Should().Be("false");
            chip.ClassList.Should().NotContain("m-oos-chip--active");
            dispatched.Should().NotBeEmpty();
            dispatched.All(q => !q.IncludeInactive).Should().BeTrue(
                "the initial mount dispatches in-stock-only queries");

            // Act — click the chip ON.
            await cut.InvokeAsync(() => FindChip(cut).Click());

            // Assert — a follow-up query dispatched with the flag, the
            // chip is active, and the URL mirror picked up oos=1 (the
            // flag survives the add-to-cart full-page reload).
            cut.WaitForAssertion(
                () => dispatched.Any(q => q.IncludeInactive)
                    .Should().BeTrue("a follow-up query carried the out-of-stock flag"),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().Contain("m-oos-chip--active");
            cut.WaitForAssertion(
                () => ctx.Services.GetRequiredService<NavigationManager>().Uri
                    .Should().Contain("oos=1", "the view mode persists in the URL for reload survival"),
                TimeSpan.FromSeconds(5));

            // The sold-out card treatment renders on the zero-stock
            // cards (and ONLY those — the in-stock cards stay normal).
            cut.FindAll(".m-product-card--out").Should().HaveCount(2);
            cut.FindAll(".m-product-card").Should().HaveCount(MobilePageSize);

            // Act — click the chip OFF again.
            await cut.InvokeAsync(() => FindChip(cut).Click());

            // Assert — the LATEST query carries no flag, the chip
            // returned to its inactive state, and the URL mirror
            // dropped the oos parameter.
            cut.WaitForAssertion(
                () => dispatched.Last().IncludeInactive.Should().BeFalse(),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("false"),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => ctx.Services.GetRequiredService<NavigationManager>().Uri
                    .Should().NotContain("oos=", "the off state cleans the URL flag"),
                TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void MobileProducts_OosDeepLink_Manager_RestoresViewMode()
    {
        // Arrange — the exact URL the chip itself persists:
        // pre-navigate BEFORE mounting so the page's OnInitializedAsync
        // URL parse sees it. Includes one zero-stock card — exactly what
        // the handler WOULD return with the flag on.
        var (ctx, dispatched, _) = Setup(
            new[] { Roles.Manager }, totalCount: 40,
            pageProvider: _ => MobileProductsPageTestHarness.MakeProducts(MobilePageSize, zeroStockCount: 1));
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/m/Products?oos=1");

            // Act — mount the page; the restore applies the view mode
            // BEFORE the first products load, so the very first query
            // already carries the flag.
            var cut = Mount(ctx, MobilePageSize);

            dispatched[0].IncludeInactive.Should().BeTrue(
                "the FIRST products query already carried the restored view mode");

            // The chip — whose state is the same field the restore set —
            // renders active without any restore-specific bookkeeping.
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindChip(cut).ClassList.Should().Contain("m-oos-chip--active");

            // The sold-out card treatment marks the zero-stock card.
            cut.FindAll(".m-product-card--out").Should().HaveCount(1);
        }
    }

    [Fact]
    public void MobileProducts_ChipHiddenForCustomerAndOosUrlIgnored()
    {
        // Arrange — a Customer opening a STAFF-SHARED ?oos=1 link: the
        // chip must not render at all, and the local flag must stay off
        // (the server-side clamp in the handler makes the ignored flag
        // harmless — this pins the UI-side half of that contract).
        var (ctx, dispatched, _) = Setup(
            new[] { Roles.Customer }, totalCount: 40,
            pageProvider: _ => MobileProductsPageTestHarness.MakeProducts(MobilePageSize));
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/m/Products?oos=1");

            var cut = Mount(ctx, MobilePageSize);

            // No chip in the markup — the toolbar has only the sort group
            // and the export button for non-staff users.
            cut.FindAll("button.m-oos-chip").Should().BeEmpty();

            // And the URL's flag never reached a query.
            cut.WaitForAssertion(
                () => dispatched.Should().NotBeEmpty(),
                TimeSpan.FromSeconds(5));
            dispatched.All(q => !q.IncludeInactive).Should().BeTrue(
                "a customer's ?oos=1 deep link never reaches the query (UI-side half of the server clamp)");
        }
    }

    [Fact]
    public async Task MobileProducts_ClearSearch_ReloadsUnfilteredListAndKeepsOos()
    {
        // Arrange — an admin who searched (?search=macaroni) with the
        // staff view mode on (?oos=1): the mount query carries BOTH.
        var (ctx, dispatched, _) = Setup(
            new[] { Roles.Admin }, totalCount: 40,
            pageProvider: _ => MobileProductsPageTestHarness.MakeProducts(MobilePageSize));
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/m/Products?search=macaroni&oos=1");

            var cut = Mount(ctx, MobilePageSize);
            dispatched[0].SearchTerm.Should().Be("macaroni");
            dispatched[0].IncludeInactive.Should().BeTrue();

            // The search-results chip (with its × clear button) rendered.
            cut.WaitForAssertion(
                () => cut.FindAll(".m-search-chip-clear").Should().HaveCount(1),
                TimeSpan.FromSeconds(5));

            // Act — clear the search, then simulate the ROUTER's
            // parameter pass. bUnit renders the page WITHOUT a Router
            // host, so FakeNavigationManager.NavigateTo alone never
            // re-triggers OnParametersSetAsync; in production the Router
            // re-renders the routed page on every same-route navigation
            // (fresh RouteData → parameter pass), which is exactly what
            // SetParametersAndRender() (empty: the page has no route
            // params) reproduces here.
            await cut.InvokeAsync(() => cut.Find(".m-search-chip-clear").Click());
            await cut.InvokeAsync(() => cut.SetParametersAndRender());

            // Assert — a follow-up query WITHOUT the search term but
            // STILL carrying the staff view mode (a view mode, not a
            // search filter — it survives the clear). REGRESSION PIN:
            // the old ClearSearch pre-cleared _searchTerm, so the
            // OnParametersSetAsync change detection never fired and the
            // list kept the stale filtered rows while the chip vanished.
            cut.WaitForAssertion(
                () => dispatched.Last().SearchTerm.Should().BeNull(
                    "clearing the search reloads the UNFILTERED list (the pre-existing stale-list bug)"),
                TimeSpan.FromSeconds(5));
            dispatched.Last().IncludeInactive.Should().BeTrue(
                "the staff view mode survives a search clear");

            // The chip is gone, the URL kept the oos mirror, and the
            // staff chip is still active.
            cut.WaitForAssertion(
                () => cut.FindAll(".m-search-chip-clear").Should().BeEmpty(),
                TimeSpan.FromSeconds(5));
            ctx.Services.GetRequiredService<NavigationManager>().Uri
                .Should().Contain("oos=1").And.NotContain("search=");
            cut.WaitForAssertion(
                () => FindChip(cut).GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
        }
    }

    // ── CSV export (the desktop shop export, ported) ────────────────

    [Fact]
    public async Task MobileProducts_Export_Click_ProducesCustomerSafeCsv()
    {
        // Arrange — a CUSTOMER (the export is deliberately NOT
        // staff-gated: the columns are customer-safe, the data is
        // role-scoped server-side). One zero-stock row (first) and a
        // comma+quote product name (second) exercise the RFC 4180
        // quoting and the availability labels. Built inline: the DTO's
        // properties are init-only (no post-construction mutation).
        var page = new List<ProductListItemDto>
        {
            new() { Id = Guid.NewGuid(), Name = "Product 001", Description = "d1",
                    StockQuantity = 0, Price = new MoneyDto { Amount = 10_000m, Currency = "IRR" } },
            new() { Id = Guid.NewGuid(), Name = "Pasta, \"Premium\" 500g", Description = "d2",
                    StockQuantity = 17, Price = new MoneyDto { Amount = 20_000m, Currency = "IRR" } },
            new() { Id = Guid.NewGuid(), Name = "Product 003", Description = "d3",
                    StockQuantity = 18, Price = new MoneyDto { Amount = 30_000m, Currency = "IRR" } }
        };
        var (ctx, _, js) = Setup(
            new[] { Roles.Customer }, totalCount: 3,
            pageProvider: _ => page);
        using (ctx)
        {
            var cut = Mount(ctx, 3);

            // Act — click the toolbar's export button.
            await cut.InvokeAsync(() => cut.Find("button.m-export-btn").Click());

            // Assert — exactly one takDownload.csv invocation.
            cut.WaitForAssertion(
                () => js.Invocations["takDownload.csv"].Should().HaveCount(1),
                TimeSpan.FromSeconds(5));
            var args = js.Invocations["takDownload.csv"][0].Arguments;
            var fileName = args[0].Should().BeOfType<string>().Subject;
            fileName.Should().StartWith("takone-catalog-").And.EndWith(".csv");

            // The CSV: header + 3 data rows. Identity-localizer keys as
            // headers; invariant amounts; the availability column uses
            // the same in/out labels the card badges show. NOTE row order:
            // MakeProducts puts the zero-stock product FIRST.
            var csv = args[1].Should().BeOfType<string>().Subject;
            // CRLF records + a trailing CRLF after the last row — the
            // trailing empty split fragment is dropped.
            var lines = csv.Split('\n').Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0).ToList();
            lines.Should().HaveCount(4);
            lines[0].Should().Be(
                "ColName,ColCategory,ColSubCategory,ColSubSubCategory,ColPrice,ColCurrency,ColAvailability");

            // Row 1 — the ZERO-STOCK row ships the OUT label, invariant
            // amount, empty category cells.
            lines[1].Should().Be("Product 001,,,,10000,IRR,StockOut");

            // Row 2 — the punctuation-laden name is RFC 4180 quoted
            // (embedded quotes doubled); in-stock label.
            lines[2].Should().Be("\"Pasta, \"\"Premium\"\" 500g\",,,,20000,IRR,StockIn");

            // Row 3 — a plain in-stock row.
            lines[3].Should().Be("Product 003,,,,30000,IRR,StockIn");
        }
    }
}

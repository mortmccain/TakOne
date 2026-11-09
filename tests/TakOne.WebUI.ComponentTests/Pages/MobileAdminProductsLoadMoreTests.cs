using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Products.DTOs;
using TakOne.Application.Products.Queries.GetProductsPaginated;
using TakOne.Application.Resources;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.MobilePages.MobileAdminProducts;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Shared bUnit harness for mounting the FULL mobile admin product-list
/// page (QA-12 round — the first page-level coverage MobileAdminProducts
/// has ever had).
/// </summary>
/// <remarks>
/// <para>
/// The page dispatches ONE query type
/// (<see cref="GetProductsPaginatedQuery"/>) — the fake bus records every
/// dispatch in order and answers through a per-page provider (the
/// sequence-based discipline of <see cref="MobileProductsPageTestHarness"/>).
/// </para>
/// <para>
/// The page resolves no roles and uses no AuthenticationStateProvider
/// (everything is route-level [Authorize(Policy)] which RenderComponent
/// bypasses) — same wiring posture as the desktop AdminProducts page
/// tests.
/// </para>
/// <para>
/// The localizer stub ALSO configures the format-args indexer (the count
/// line uses <c>Loc["ShowingCount", a, b, c]</c>).
/// </para>
/// </remarks>
internal static class MobileAdminProductsPageTestHarness
{
    /// <summary>
    /// Mounts the full mobile admin products page. Returns the test
    /// context and the ordered list of every dispatched
    /// <see cref="GetProductsPaginatedQuery"/>.
    /// </summary>
    /// <param name="totalCount">The canned TotalCount for every products
    /// page (drives the count line + load-more visibility).</param>
    /// <param name="pageProvider">Answers each products query with the
    /// payload for ITS page number.</param>
    internal static (BunitContext ctx, List<GetProductsPaginatedQuery> dispatched) Setup(
        int totalCount,
        Func<GetProductsPaginatedQuery, List<ProductListItemDto>>? pageProvider = null)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer with the format-args twin (count line).
        var loc = Substitute.For<IStringLocalizer<MobileAdminProducts>>();
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

        // ToastService + ErrorDisplayService (page injects both).
        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        // The page injects Radzen DialogService (restock/deactivate
        // flows — not exercised here).
        ComponentTestSetup.AddDialogServiceSpy(ctx);

        // The fake bus: canned answers through the per-page provider,
        // plus a RECORD of every products query, in order.
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

        ctx.Services.AddSingleton(bus);

        return (ctx, dispatched);
    }

    /// <summary>
    /// Makes <paramref name="count"/> distinct products (offset lets
    /// page 2+ payloads carry different names/ids than page 1).
    /// </summary>
    internal static List<ProductListItemDto> MakeProducts(int count, int offset = 0) =>
        Enumerable.Range(1, count).Select(i =>
        {
            var n = offset + i;
            return new ProductListItemDto
            {
                Id = Guid.NewGuid(),
                Name = $"Product {n:000}",
                Description = $"Description {n}",
                StockQuantity = 15 + i,
                Price = new MoneyDto { Amount = n * 10_000m, Currency = "IRR" },
                CategoryName = "Test Category",
                CategoryIsActive = true
            };
        }).ToList();
}

/// <summary>
/// Page-level bUnit tests for the QA-12 mobile admin product-list round:
/// append-style "load more" pagination (the page previously loaded ONE
/// silent 100-row page — products past #100 were invisible on mobile)
/// and the dedicated filtered-to-zero message with its clear-filters
/// escape hatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. PAGINATION: every dispatched query carries
///      <c>IncludeInactive = true</c> (the staff view — the page's
///      contract), the count line reflects the loaded/total split,
///      "Load more" appends the NEXT page as a follow-up query, and the
///      button hides at the end.
///   2. NO-MATCHES COPY: a search matching zero rows over a NON-empty
///      catalog shows the DEDICATED NoMatchesMessage (not the true-empty
///      "Create your first product" copy + CTA), the not-loaded-yet
///      hint while pages remain unloaded, and a Clear-filters button
///      that restores the rows without a round-trip.
/// </para>
/// <para>
/// <b>WIRING</b>: see <see cref="MobileAdminProductsPageTestHarness"/>.
/// Assertion style: sequence-based (the recording fake bus) — re-Find
/// elements before each event dispatch (Blazor re-renders after every
/// dispatch; captured references go stale).
/// </para>
/// </remarks>
public class MobileAdminProductsLoadMoreTests
{
    // The mobile page's loaded-window page size (mirrors the page const).
    private const int ProductsPageSize = 100;

    private static (BunitContext ctx, List<GetProductsPaginatedQuery> dispatched) Setup(
        int totalCount,
        Func<GetProductsPaginatedQuery, List<ProductListItemDto>>? pageProvider = null)
        => MobileAdminProductsPageTestHarness.Setup(totalCount, pageProvider);

    /// <summary> Renders and waits for the initial list rows. </summary>
    private static IRenderedComponent<MobileAdminProducts> Mount(BunitContext ctx, int expectedRows)
    {
        var cut = ctx.Render<MobileAdminProducts>();
        cut.WaitForAssertion(
            () => cut.FindAll(".m-list-item").Should().HaveCount(expectedRows),
            TimeSpan.FromSeconds(10));
        return cut;
    }

    [Fact]
    public async Task MobileAdminProducts_LoadMore_AppendsNextPageAndHidesAtEnd()
    {
        // Arrange — a 250-product catalog, 100 per page; the provider
        // hands out DISTINCT payloads per page so appension is
        // observable in the rendered row count.
        const int total = 250;
        var (ctx, dispatched) = Setup(
            totalCount: total,
            pageProvider: q =>
            {
                var already = (q.PageNumber - 1) * ProductsPageSize;
                return MobileAdminProductsPageTestHarness.MakeProducts(
                    Math.Min(ProductsPageSize, total - already), offset: already);
            });
        using (ctx)
        {
            var cut = Mount(ctx, ProductsPageSize);

            // The initial mount asked for page 1 of 100 with the staff
            // view (IncludeInactive=true — the page's contract).
            dispatched.Should().HaveCountGreaterThanOrEqualTo(1);
            dispatched[0].PageNumber.Should().Be(1);
            dispatched[0].PageSize.Should().Be(ProductsPageSize);
            dispatched[0].IncludeInactive.Should().BeTrue();

            // Count line reflects the loaded/total split.
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{ProductsPageSize}|{total}");

            // Act — Load more: appends page 2.
            await cut.InvokeAsync(() => cut.Find("button.m-load-more-btn").Click());

            // Assert — appended rows + advanced count line + follow-up
            // query pinned to page 2, still carrying the staff view.
            cut.WaitForAssertion(
                () => cut.FindAll(".m-list-item").Should().HaveCount(ProductsPageSize * 2),
                TimeSpan.FromSeconds(5));
            dispatched.Last().PageNumber.Should().Be(2);
            dispatched.Last().PageSize.Should().Be(ProductsPageSize);
            dispatched.Last().IncludeInactive.Should().BeTrue();
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{ProductsPageSize * 2}|{total}");

            // Act again — the final page (50 rows) closes the window.
            await cut.InvokeAsync(() => cut.Find("button.m-load-more-btn").Click());

            cut.WaitForAssertion(
                () => cut.FindAll(".m-list-item").Should().HaveCount(total),
                TimeSpan.FromSeconds(5));
            dispatched.Last().PageNumber.Should().Be(3);
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{total}|{total}");

            // The button hides once the catalog is loaded — the staff
            // list is complete, not silently truncated at 100 anymore.
            cut.WaitForAssertion(
                () => cut.FindAll("button.m-load-more-btn").Should().BeEmpty(),
                TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MobileAdminProducts_NoMatches_SearchShowsDedicatedMessageAndClearRestores()
    {
        // Arrange — a 250-product catalog with only page 1 loaded.
        const int total = 250;
        var (ctx, _) = Setup(
            totalCount: total,
            pageProvider: _ => MobileAdminProductsPageTestHarness.MakeProducts(ProductsPageSize));
        using (ctx)
        {
            var cut = Mount(ctx, ProductsPageSize);

            // Act — search for something that matches nothing.
            await cut.InvokeAsync(() => cut.Find("input[type=search]").Input("zzz-no-match"));

            // Assert — the DEDICATED no-matches message (NOT the
            // true-empty "Create your first product" copy + CTA), the
            // not-loaded-yet hint (window 100 < 250), and the
            // clear-filters escape hatch.
            cut.WaitForAssertion(
                () => cut.Find(".m-empty").TextContent.Should().Contain("NoMatchesMessage"),
                TimeSpan.FromSeconds(5));
            cut.Find(".m-empty").TextContent.Should().Contain("NoMatchesMoreHint");
            cut.Find(".m-empty .m-clear-filters-btn").Should().NotBeNull();

            // Act — clear the filters.
            await cut.InvokeAsync(() => cut.Find(".m-empty .m-clear-filters-btn").Click());

            // Assert — the rows return with no round-trip.
            cut.WaitForAssertion(
                () => cut.FindAll(".m-list-item").Should().HaveCount(ProductsPageSize),
                TimeSpan.FromSeconds(5));
            cut.FindAll(".m-empty").Should().BeEmpty();
        }
    }
}

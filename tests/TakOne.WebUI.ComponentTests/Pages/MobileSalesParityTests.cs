using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Resources;
using TakOne.Application.Sales.DTOs;
using TakOne.Application.Sales.Queries.GetSalesPaginated;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.MobilePages.MobileSales;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Shared bUnit harness for mounting the FULL mobile order-history page
/// (QA-12 round — the first page-level coverage MobileSales has ever
/// had).
/// </summary>
/// <remarks>
/// <para>
/// The page dispatches ONE query type through the message bus
/// (<see cref="GetSalesPaginatedQuery"/>) — the fake bus records every
/// dispatch in order and answers through a per-page provider, so
/// append-pagination assertions can distinguish page 1 from page 2/3
/// payloads (the same sequence-based discipline as
/// <see cref="MobileProductsPageTestHarness"/>).
/// </para>
/// <para>
/// Roles are wired through the claims-based
/// <see cref="ComponentTestSetup.AddAuthenticatedUser"/> — the page
/// resolves <c>_isStaff</c> from the AuthenticationStateProvider's
/// principal (<c>IsInRole</c>), not ICurrentUserService, and renders no
/// AuthorizeView (the route-level [Authorize] is enforced by the Router,
/// which RenderComponent bypasses).
/// </para>
/// <para>
/// The localizer stub ALSO configures the format-args indexer (the count
/// line uses <c>Loc["ShowingCount", a, b, c]</c>); args render as
/// <c>arg0|arg1|…</c> so formatted output is assertable.
/// </para>
/// </remarks>
internal static class MobileSalesPageTestHarness
{
    /// <summary>
    /// Mounts the full mobile order-history page with the supplied role
    /// set. Returns the test context, the ordered list of every
    /// dispatched <see cref="GetSalesPaginatedQuery"/>, and the bUnit JS
    /// interop (loose mode — the CSV export's takDownload.csv
    /// invocations land in the global invocation dictionary).
    /// </summary>
    /// <param name="roles">The caller's role set (drives the page's
    /// staff-vs-customer row layout and the export's audit columns).</param>
    /// <param name="totalCount">The canned TotalCount for every sales
    /// page (drives the count line + load-more visibility).</param>
    /// <param name="pageProvider">Answers each sales query with the
    /// payload for ITS page number — lets append-pagination tests hand
    /// out distinct payloads per page.</param>
    internal static (TestContext ctx, List<GetSalesPaginatedQuery> dispatched, BunitJSInterop js) Setup(
        string[] roles,
        int totalCount,
        Func<GetSalesPaginatedQuery, List<SaleListItemDto>>? pageProvider = null)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer for the page's own Loc[...] lookups, with
        // the format-args twin (renders args as a|b|c — see remarks).
        var loc = Substitute.For<IStringLocalizer<MobileSales>>();
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

        // ToastService (the export round-trip toasts its outcome).
        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        // Claims-based provider — the page's _isStaff runs on
        // principal.IsInRole.
        ComponentTestSetup.AddAuthenticatedUser(ctx, "tester", roles);

        // The fake bus: canned answers through the per-page provider,
        // plus a RECORD of every sales query, in order.
        var provider = pageProvider ?? (_ => MakeSales(10));
        var dispatched = new List<GetSalesPaginatedQuery>();
        var bus = Substitute.For<IMessageBus>();

        bus.InvokeAsync<PaginatedResult<SaleListItemDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.ArgAt<object>(0).Should().BeOfType<GetSalesPaginatedQuery>().Subject;
                List<SaleListItemDto> page;
                lock (dispatched)
                {
                    dispatched.Add(query);
                    page = provider(query);
                }
                return new PaginatedResult<SaleListItemDto>(page, totalCount, query.PageNumber, query.PageSize);
            });

        ctx.Services.AddSingleton(bus);

        return (ctx, dispatched, ctx.JSInterop);
    }

    /// <summary>
    /// Makes <paramref name="count"/> distinct sales (offset lets
    /// page 2+ payloads carry different numbers/ids than page 1). The
    /// first sale of the first page can carry a CSV-hostile SaleNumber
    /// (comma + quotes) to pin RFC 4180 quoting in the export test.
    /// </summary>
    internal static List<SaleListItemDto> MakeSales(int count, int offset = 0, bool hostileFirstNumber = false) =>
        Enumerable.Range(1, count).Select(i =>
        {
            var n = offset + i;
            return new SaleListItemDto
            {
                Id = Guid.NewGuid(),
                SaleNumber = hostileFirstNumber && n == 1
                    ? "ORD, \"42\""
                    : $"INT-1405-{n:00000000}",
                Status = "Approved",
                CustomerName = $"Customer {n:000}",
                CreatedByName = $"Staff {n:000}",
                CreatedAtUtc = new DateTimeOffset(2026, 10, 3, 9, 30, 0, TimeSpan.Zero).AddHours(n),
                Total = new MoneyDto { Amount = n * 1_000m, Currency = "IRR" }
            };
        }).ToList();
}

/// <summary>
/// Page-level bUnit tests for the QA-12 mobile order-history round:
/// append-style "load more" pagination (the page previously loaded ONE
/// silent 100-row page — a customer with 250 orders could never reach
/// #101+), the dedicated filtered-to-zero message with its clear-filters
/// escape hatch, and the full-history CSV export.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. PAGINATION: the count line reflects the loaded/total split;
///      "Load more" appends the NEXT page as a follow-up query; the
///      button hides at the end; a single-page history hides both.
///   2. NO-MATCHES COPY: a search matching zero rows over a NON-empty
///      list shows the DEDICATED NoMatchesMessage (not the true-empty
///      "Your order history will appear here" copy), the load-more
///      hint while pages remain unloaded, and a Clear-filters button
///      that restores the rows without a round-trip.
///   3. EXPORT: the download loops ALL pages of the server-scoped
///      history (not just the loaded window), producing the desktop-
///      twin column shape (no staff audit columns for a customer
///      viewer) via the shared takDownload.csv pipeline with RFC 4180
///      quoting, RAW amounts, and ISO-8601 timestamps.
/// </para>
/// <para>
/// <b>WIRING</b>: see <see cref="MobileSalesPageTestHarness"/>.
/// Assertion style: sequence-based (the recording fake bus) — re-Find
/// elements before each event dispatch (Blazor re-renders after every
/// dispatch; captured references go stale).
/// </para>
/// </remarks>
public class MobileSalesParityTests
{
    // The mobile page's loaded-window page size (mirrors the page const).
    private const int SalesPageSize = 100;

    private static (TestContext ctx, List<GetSalesPaginatedQuery> dispatched, BunitJSInterop js) Setup(
        string[] roles,
        int totalCount,
        Func<GetSalesPaginatedQuery, List<SaleListItemDto>>? pageProvider = null)
        => MobileSalesPageTestHarness.Setup(roles, totalCount, pageProvider);

    /// <summary> Renders and waits for the initial list rows. </summary>
    private static IRenderedComponent<MobileSales> Mount(TestContext ctx, int expectedRows)
    {
        var cut = ctx.RenderComponent<MobileSales>();
        cut.WaitForAssertion(
            () => cut.FindAll(".m-list-item").Should().HaveCount(expectedRows),
            TimeSpan.FromSeconds(10));
        return cut;
    }

    // ── Pagination ──────────────────────────────────────────────────

    [Fact]
    public async Task MobileSales_LoadMore_AppendsNextPageAndHidesAtEnd()
    {
        // Arrange — a 250-sale history, 100 per page; the provider hands
        // out DISTINCT payloads per page (offset by page number) so
        // appension is observable in the rendered row count.
        const int total = 250;
        var (ctx, dispatched, _) = Setup(
            new[] { Roles.Customer },
            totalCount: total,
            pageProvider: q =>
            {
                var already = (q.PageNumber - 1) * SalesPageSize;
                return MobileSalesPageTestHarness.MakeSales(
                    Math.Min(SalesPageSize, total - already), offset: already);
            });
        using (ctx)
        {
            var cut = Mount(ctx, SalesPageSize);

            // The initial mount asked for page 1 of 100.
            dispatched.Should().HaveCountGreaterThanOrEqualTo(1);
            dispatched[0].PageNumber.Should().Be(1);
            dispatched[0].PageSize.Should().Be(SalesPageSize);

            // Count line reflects the loaded/total split (the format-args
            // stub renders args as a|b|c: first, last, total).
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{SalesPageSize}|{total}");

            // Act — Load more: appends page 2.
            await cut.InvokeAsync(() => cut.Find("button.m-load-more-btn").Click());

            // Assert — a follow-up query for page 2 (100 more rows), the
            // list APPENDED (200 rows), and the count line advanced.
            cut.WaitForAssertion(
                () => cut.FindAll(".m-list-item").Should().HaveCount(SalesPageSize * 2),
                TimeSpan.FromSeconds(5));
            dispatched.Last().PageNumber.Should().Be(2);
            dispatched.Last().PageSize.Should().Be(SalesPageSize);
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{SalesPageSize * 2}|{total}");

            // Act again — the final page (50 rows) closes the window.
            await cut.InvokeAsync(() => cut.Find("button.m-load-more-btn").Click());

            cut.WaitForAssertion(
                () => cut.FindAll(".m-list-item").Should().HaveCount(total),
                TimeSpan.FromSeconds(5));
            dispatched.Last().PageNumber.Should().Be(3);
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{total}|{total}");

            // The button hides once everything is loaded — the history is
            // complete, not silently truncated at 100 anymore.
            cut.WaitForAssertion(
                () => cut.FindAll("button.m-load-more-btn").Should().BeEmpty(),
                TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void MobileSales_SinglePageHistory_ShowsCountButHidesLoadMore()
    {
        // Arrange — an 80-sale history: fits in one page.
        const int total = 80;
        var (ctx, dispatched, _) = Setup(
            new[] { Roles.Customer },
            totalCount: total,
            pageProvider: _ => MobileSalesPageTestHarness.MakeSales(total));
        using (ctx)
        {
            var cut = Mount(ctx, total);

            // Single-page history: the count line stays (informational —
            // "Showing 1–80 of 80" tells the customer the history is
            // complete; the same QA-11 convention as the shop page) but
            // the Load-more button hides — zero pagination noise.
            cut.Find(".m-list-count").TextContent.Trim().Should().Be($"1|{total}|{total}");
            cut.FindAll("button.m-load-more-btn").Should().BeEmpty();
            dispatched.Should().HaveCount(1);
        }
    }

    // ── Filtered-to-zero copy + escape hatch ────────────────────────

    [Fact]
    public async Task MobileSales_NoMatches_SearchShowsDedicatedMessageAndClearRestores()
    {
        // Arrange — a 250-sale history with only page 1 loaded.
        const int total = 250;
        var (ctx, _, _) = Setup(
            new[] { Roles.Customer },
            totalCount: total,
            pageProvider: _ => MobileSalesPageTestHarness.MakeSales(SalesPageSize));
        using (ctx)
        {
            var cut = Mount(ctx, SalesPageSize);

            // Act — search for something that matches nothing.
            await cut.InvokeAsync(() => cut.Find("input[type=search]").Input("zzz-no-match"));

            // Assert — the DEDICATED no-matches message (NOT the
            // true-empty "history will appear here" copy), the
            // not-loaded-yet hint (window 100 < 250), and the
            // clear-filters escape hatch.
            cut.WaitForAssertion(
                () => cut.Find(".m-empty").TextContent.Should().Contain("NoMatchesMessage"),
                TimeSpan.FromSeconds(5));
            cut.Find(".m-empty").TextContent.Should().Contain("NoMatchesMoreHint");
            cut.Find(".m-empty .m-clear-filters-btn").Should().NotBeNull();

            // Act — clear the filters.
            await cut.InvokeAsync(() => cut.Find(".m-empty .m-clear-filters-btn").Click());

            // Assert — the rows return with no round-trip (pure client
            // state reset; the escape hatch does exactly what it says).
            cut.WaitForAssertion(
                () => cut.FindAll(".m-list-item").Should().HaveCount(SalesPageSize),
                TimeSpan.FromSeconds(5));
            cut.FindAll(".m-empty").Should().BeEmpty();
        }
    }

    // ── CSV export ─────────────────────────────────────────────────

    [Fact]
    public async Task MobileSales_ExportClick_DownloadsFullHistoryCsv()
    {
        // Arrange — a 150-sale history for a CUSTOMER (the export must
        // be customer-visible: it's their own order history take-away;
        // the server scopes the query, and the audit columns drop).
        const int total = 150;
        var (ctx, dispatched, js) = Setup(
            new[] { Roles.Customer },
            totalCount: total,
            pageProvider: q =>
            {
                var already = (q.PageNumber - 1) * SalesPageSize;
                return MobileSalesPageTestHarness.MakeSales(
                    Math.Min(SalesPageSize, total - already),
                    offset: already,
                    hostileFirstNumber: q.PageNumber == 1);
            });
        using (ctx)
        {
            var cut = Mount(ctx, SalesPageSize);

            // Act — click the export button (in the filter bar next to
            // the search box).
            await cut.InvokeAsync(() => cut.Find("button.m-export-btn").Click());

            // Assert — exactly one takDownload.csv invocation.
            cut.WaitForAssertion(
                () => js.Invocations["takDownload.csv"].Should().HaveCount(1),
                TimeSpan.FromSeconds(10));

            var args = js.Invocations["takDownload.csv"][0].Arguments;
            args.Should().HaveCount(2);
            args[0].Should().BeOfType<string>().Which
                .Should().StartWith("takone-sales-")
                .And.EndWith(".csv");

            // The export looped ALL pages (1 initial load + pages 1..2
            // of the export loop = 3 dispatches) — the download carries
            // the FULL 150-row history, not just the loaded 100.
            cut.WaitForAssertion(
                () => dispatched.Should().HaveCount(3),
                TimeSpan.FromSeconds(5));
            dispatched[1].PageNumber.Should().Be(1);
            dispatched[2].PageNumber.Should().Be(2);

            // The CSV: 5 customer columns (number, status, total,
            // currency, created-at) — NO staff audit columns for a
            // customer viewer.
            var csv = args[1].Should().BeOfType<string>().Subject;
            csv.Should().StartWith(
                "ColSaleNumber,ColStatus,ColTotal,ColCurrency,ColCreatedAt\r\n");

            // Row 1: RFC 4180 quoting of the hostile sale number (comma
            // + escaped quotes), RAW invariant amount, ISO-8601 UTC
            // timestamp. The status label renders as its resx key under
            // the identity localizer.
            csv.Should().Contain(
                "\"ORD, \"\"42\"\"\",StatusApproved,1000,IRR,2026-10-03 10:30:00Z\r\n");

            // A later row (offset sale #101 from export page 2 — proving
            // the loop crossed the page boundary). Its timestamp is the
            // base + 101 hours = 4 days + 5 hours later.
            csv.Should().Contain(
                "INT-1405-00000101,StatusApproved,101000,IRR,2026-10-07 14:30:00Z\r\n");
        }
    }
}

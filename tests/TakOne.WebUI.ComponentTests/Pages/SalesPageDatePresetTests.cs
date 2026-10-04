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
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.Pages.Sales;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit tests for the Sales page's date-range PRESET chips
/// (Today / last 7 days / last 30 days).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. CLICKING a preset drives the toolbar's from/to contract — a
///      follow-up dispatched <see cref="GetSalesPaginatedQuery"/> carries
///      the Tehran-local UTC half-open bounds of the preset, the applied-
///      range chip appears, and only the clicked preset's chip activates
///      (aria-pressed + the active modifier class).
///   2. The URL deep link (?from=&amp;to= with today's Tehran-local date)
///      restores the same bounds on mount through the page's existing URL
///      state contract, and the matching preset chip DERIVES its active
///      state from the restored applied bounds — no bookkeeping.
///   3. Clicking a DIFFERENT preset switches the applied range (a preset
///      is a one-shot selector, not a toggle).
/// </para>
/// <para>
/// <b>TEHRAN-LOCAL BOUNDS</b>: the page computes "today" as
/// <c>UtcNow + 03:30</c> (Tehran abolished DST in 2022) and applies
/// [local-midnight, next-local-midnight) as UTC instants. The tests
/// recompute the same arithmetic and assert exact equality — pinning the
/// "today means Tehran today, not server-clock today" semantics.
/// </para>
/// <para>
/// <b>ASSERTION STYLE</b>: sequence-based (the recording fake bus) — see
/// <see cref="SalesPagePendingChipTests"/> for the rationale (Radzen may
/// re-fire LoadData; the contract is "the bounds reach the query").
/// </para>
/// </remarks>
public class SalesPageDatePresetTests
{
    /// <summary>Tehran's fixed UTC offset (matches Sales.razor's constant).</summary>
    private static readonly TimeSpan TehranOffset = TimeSpan.FromHours(3.5);

    private static List<SaleListItemDto> MakeSales(int count) =>
        Enumerable.Range(1, count).Select(i => new SaleListItemDto
        {
            Id = Guid.NewGuid(),
            SaleNumber = $"INT-1405-{i:00000000}",
            CustomerName = $"Customer {i:000}",
            Status = i % 2 == 0 ? "Pending" : "Approved",
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
        ComponentTestSetup.AddIdentityLocalizer<Sales>(ctx);

        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        // AddRadzenComponents registers NotificationService as SCOPED —
        // override with a singleton so the ToastService factory resolves.
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        ComponentTestSetup.AddAuthenticatedUser(ctx, "tester", TakOne.Application.Common.Authorization.Roles.Admin);

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

    /// <summary> The toolbar's preset chips, in render order (Today, 7d, 30d). </summary>
    private static IReadOnlyList<IElement> FindPresetChips(IRenderedComponent<Sales> cut)
        => cut.FindAll("button.sales-preset-chip");

    /// <summary>
    /// Expected UTC bounds of a preset applied for "today" (Tehran-local):
    /// [today's local midnight, tomorrow's local midnight) — the exact
    /// arithmetic Sales.razor's ToUtcInstant performs.
    /// </summary>
    private static (DateTime FromUtc, DateTime ToUtc) ExpectedBounds(int daysBack)
    {
        var today = (DateTime.UtcNow + TehranOffset).Date;
        var from = today.AddDays(-daysBack);
        return (
            DateTime.SpecifyKind(from, DateTimeKind.Utc) - TehranOffset,
            DateTime.SpecifyKind(today.AddDays(1), DateTimeKind.Utc) - TehranOffset);
    }

    [Fact]
    public async Task Sales_TodayPresetClick_AppliesTehranTodayBounds()
    {
        var (ctx, dispatched) = Setup(totalCount: 45, page: MakeSales(20));
        using (ctx)
        {
            var cut = ctx.RenderComponent<Sales>();
            cut.WaitForAssertion(
                () => cut.FindAll(".sale-number-link").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Initially no preset is active: no date filter on mount.
            FindPresetChips(cut).Should().HaveCount(3);
            FindPresetChips(cut)[0].GetAttribute("aria-pressed").Should().Be("false");
            dispatched.All(q => q.FromDateUtc is null && q.ToDateUtc is null).Should().BeTrue(
                "the initial mount dispatches unfiltered queries only");

            // Act — click the FIRST preset chip (Today).
            await cut.InvokeAsync(() => FindPresetChips(cut)[0].Click());

            // Assert — a follow-up query carries today's Tehran bounds,
            // the Today chip (and only it) activates, and the applied-
            // range chip renders the range it can now clear.
            var (expectedFrom, expectedTo) = ExpectedBounds(daysBack: 0);
            cut.WaitForAssertion(
                () => dispatched.Any(q => q.FromDateUtc == expectedFrom && q.ToDateUtc == expectedTo)
                    .Should().BeTrue("a follow-up query carried the today bounds"),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindPresetChips(cut)[0].GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindPresetChips(cut)[0].ClassList.Should().Contain("sales-preset-chip--active");
            FindPresetChips(cut)[1].GetAttribute("aria-pressed").Should().Be("false");
            FindPresetChips(cut)[2].GetAttribute("aria-pressed").Should().Be("false");
            cut.FindAll(".sales-datefilter-chip").Should().HaveCount(1,
                "the applied-range chip renders while a preset's range is applied");
        }
    }

    [Fact]
    public async Task Sales_Last30DaysPresetClick_SwitchesAppliedRange()
    {
        var (ctx, dispatched) = Setup(totalCount: 45, page: MakeSales(20));
        using (ctx)
        {
            var cut = ctx.RenderComponent<Sales>();
            cut.WaitForAssertion(
                () => cut.FindAll(".sale-number-link").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Act — apply Today first, then switch to the 30-day preset
            // (the LAST chip): a preset is a one-shot selector, not a
            // toggle, so the second click REPLACES the applied range.
            await cut.InvokeAsync(() => FindPresetChips(cut)[0].Click());
            cut.WaitForAssertion(
                () => FindPresetChips(cut)[0].GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));

            await cut.InvokeAsync(() => FindPresetChips(cut)[2].Click());

            // Assert — the 30-day bounds (today − 29 .. today, Tehran)
            // reach the query and the active state moves to chip 3.
            var (expectedFrom, expectedTo) = ExpectedBounds(daysBack: 29);
            cut.WaitForAssertion(
                () => dispatched.Any(q => q.FromDateUtc == expectedFrom && q.ToDateUtc == expectedTo)
                    .Should().BeTrue("a follow-up query carried the 30-day bounds"),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => FindPresetChips(cut)[2].GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindPresetChips(cut)[2].ClassList.Should().Contain("sales-preset-chip--active");
            FindPresetChips(cut)[0].GetAttribute("aria-pressed").Should().Be("false");
        }
    }

    [Fact]
    public void Sales_TodayDeepLink_RestoresRangeAndActivatesPresetChip()
    {
        var (ctx, dispatched) = Setup(totalCount: 45, page: MakeSales(20));
        using (ctx)
        {
            // Arrange — a URL carrying exactly today's Tehran-local range
            // (the from/to state contract the presets share with the
            // pickers), pre-navigated BEFORE mounting.
            var today = (DateTime.UtcNow + TehranOffset).Date;
            var url = $"/Sales?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}";
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(url);

            var cut = ctx.RenderComponent<Sales>();
            cut.WaitForAssertion(
                () => cut.FindAll(".sale-number-link").Should().HaveCount(20),
                TimeSpan.FromSeconds(10));

            // Assert — the restored bounds reach the dispatched query.
            var (expectedFrom, expectedTo) = ExpectedBounds(daysBack: 0);
            cut.WaitForAssertion(
                () => dispatched.Any(q => q.FromDateUtc == expectedFrom && q.ToDateUtc == expectedTo)
                    .Should().BeTrue("a dispatched query carried the restored today bounds"),
                TimeSpan.FromSeconds(5));

            // The Today chip — whose state DERIVES from the applied
            // bounds — renders active without any restore-specific
            // bookkeeping; the others stay inactive.
            cut.WaitForAssertion(
                () => FindPresetChips(cut)[0].GetAttribute("aria-pressed").Should().Be("true"),
                TimeSpan.FromSeconds(5));
            FindPresetChips(cut)[1].GetAttribute("aria-pressed").Should().Be("false");
            FindPresetChips(cut)[2].GetAttribute("aria-pressed").Should().Be("false");
        }
    }
}

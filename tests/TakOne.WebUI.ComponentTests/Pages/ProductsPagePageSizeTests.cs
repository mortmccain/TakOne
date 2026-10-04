using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Products.DTOs;
using TakOne.Application.Products.Queries.GetProductsPaginated;
using TakOne.SharedKernel.Common;
using TakOne.WebUI.Components.Pages.Products;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit tests for the shop Products page's "per page"
/// selector (QA-10 — the page-size select in the pager row: 12/24/48,
/// persisted as size=N in the URL state contract).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>:
///   1. CHANGING the size dispatches a follow-up
///      <see cref="GetProductsPaginatedQuery"/> with the picked
///      <c>PageSize</c> and restarted <c>PageNumber = 1</c> (page 5 of
///      a 12-card list means something entirely different at 48), and
///      the URL mirror gains the size parameter.
///   2. The DEEP LINK (<c>?size=48</c> — the URL the selector itself
///      persists) restores the size BEFORE the first products load, so
///      the very first query already carries it.
///   3. An UNKNOWN value in a hand-edited link (<c>?size=999</c>) is
///      ignored — the page only ever accepts the three design-checked
///      choices; the server-side clamp (1..100) is the second line of
///      defense, not the first.
/// </para>
/// <para>
/// <b>WIRING</b>: see <see cref="ProductsPageTestHarness"/> (the shared
/// full-page mount). Assertion style: sequence-based (the recording
/// fake bus).
/// </para>
/// </remarks>
public class ProductsPagePageSizeTests
{
    private static List<ProductListItemDto> MakeProducts(int count) =>
        Enumerable.Range(1, count).Select(i => new ProductListItemDto
        {
            Id = Guid.NewGuid(),
            Name = $"Product {i:000}",
            StockQuantity = 15 + i,
            Price = new TakOne.SharedKernel.DTOs.MoneyDto { Amount = i * 10_000m, Currency = "IRR" }
        }).ToList();

    [Fact]
    public async Task Products_PageSizeChange_DispatchesNewSizeAndRestartsAtPage1()
    {
        // Arrange — a big catalog (200 > 12 → the pager row, and its
        // selector, render).
        var (ctx, dispatched) = ProductsPageTestHarness.Setup(
            new[] { Roles.Customer }, totalCount: 200, page: MakeProducts(12));
        using (ctx)
        {
            var cut = ctx.RenderComponent<Products>();
            cut.WaitForAssertion(
                () => cut.FindAll(".tm-product-card").Should().HaveCount(12),
                TimeSpan.FromSeconds(10));
            cut.WaitForAssertion(
                () => cut.Find("#tm-products-page-size").Should().NotBeNull(),
                TimeSpan.FromSeconds(5));

            // The mount dispatched 12-row queries (the design default).
            dispatched.All(q => q.PageSize == 12).Should().BeTrue(
                "the default page size is the 12-card design default");

            // Act — pick 24 via a real change event.
            await cut.InvokeAsync(() => cut.Find("#tm-products-page-size").Change("24"));

            // Assert — a follow-up query with PageSize 24 restarting at
            // page 1, and the URL mirror gained size=24.
            cut.WaitForAssertion(
                () => dispatched.Any(q => q.PageSize == 24)
                    .Should().BeTrue("a follow-up query carried the picked page size"),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => dispatched.Last(q => q.PageSize == 24).PageNumber.Should().Be(1,
                    "changing the page size restarts at page 1"),
                TimeSpan.FromSeconds(5));
            cut.WaitForAssertion(
                () => ctx.Services.GetRequiredService<NavigationManager>().Uri
                    .Should().Contain("size=24", "the pick persists in the URL for refresh survival"),
                TimeSpan.FromSeconds(5));

            // The select's own value reflects the pick (the markup the
            // next render round-trips).
            cut.Find("#tm-products-page-size").GetAttribute("value").Should().Be("24");
        }
    }

    [Fact]
    public void Products_PageSizeDeepLink_RestoresSizeForFirstQuery()
    {
        // Arrange — the exact URL the selector persists: pre-navigate
        // BEFORE mounting so the page's OnInitializedAsync URL parse
        // sees it.
        var (ctx, dispatched) = ProductsPageTestHarness.Setup(
            new[] { Roles.Customer }, totalCount: 200, page: MakeProducts(12));
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/Products?size=48");

            // Act — mount the page.
            var cut = ctx.RenderComponent<Products>();
            cut.WaitForAssertion(
                () => cut.FindAll(".tm-product-card").Should().HaveCount(12),
                TimeSpan.FromSeconds(10));

            // Assert — the FIRST products query already carried the
            // restored size (the restore runs before the initial loads).
            cut.WaitForAssertion(
                () => dispatched.Should().NotBeEmpty(),
                TimeSpan.FromSeconds(5));
            dispatched[0].PageSize.Should().Be(48,
                "the FIRST products query already carried the restored page size");

            // And the select renders the restored value.
            cut.WaitForAssertion(
                () => cut.Find("#tm-products-page-size").GetAttribute("value").Should().Be("48"),
                TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Products_PageSizeDeepLinkUnknownValue_Ignored()
    {
        // Arrange — a hand-edited link with a value outside the three
        // design-checked choices.
        var (ctx, dispatched) = ProductsPageTestHarness.Setup(
            new[] { Roles.Customer }, totalCount: 200, page: MakeProducts(12));
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>()
                .NavigateTo("/Products?size=999");

            var cut = ctx.RenderComponent<Products>();
            cut.WaitForAssertion(
                () => cut.FindAll(".tm-product-card").Should().HaveCount(12),
                TimeSpan.FromSeconds(10));

            // The unknown value never reached a query — the default won.
            cut.WaitForAssertion(
                () => dispatched.Should().NotBeEmpty(),
                TimeSpan.FromSeconds(5));
            dispatched.All(q => q.PageSize == 12).Should().BeTrue(
                "an unknown size in the URL keeps the design default");
        }
    }
}

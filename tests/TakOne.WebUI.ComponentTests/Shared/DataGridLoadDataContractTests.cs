using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace TakOne.WebUI.ComponentTests.SharedComponents;
/// <summary>
/// Contract probe for the Radzen <see cref="RadzenDataGrid{TItem}"/>
/// LoadData (server-driven) mode used by the Sales list page.
///
/// WHY THIS EXISTS: the Sales.razor grid was migrated from
/// "load ≤100 rows once, filter/sort/page client-side" to true
/// server-driven paging (Round 4). That migration depends on a
/// set of Radzen behaviors that the Radzen XML docs describe only
/// partially:
///   1. When <c>LoadData</c> is set, the grid raises it on init
///      with <c>args.Skip = 0</c> and <c>args.Top = PageSize</c>.
///   2. The pager renders its total from the <c>Count</c>
///      parameter (the server-side total), NOT from
///      <c>Data.Count()</c>.
///   3. Clicking a pager page re-raises LoadData with
///      <c>args.Skip = (page-1) * PageSize</c>.
///   4. With no OrderBy, <c>args.OrderBy</c> is null (the page
///      applies its own default server sort).
///
/// These tests pin the contract against the installed Radzen
/// version (11.1.8) so a Radzen upgrade that changes any of these
/// behaviors fails loudly here, at the probe, instead of silently
/// breaking the Sales grid's pagination math.
/// </summary>
public class DataGridLoadDataContractTests
{
    private sealed class Row
    {
        public string Name { get; set; } = string.Empty;
        public int Amount { get; set; }
    }

    private static List<Row> MakeRows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Row { Name = $"Row {i:000}", Amount = i })
            .ToList();

    [Fact]
    public void LoadData_OnInit_FiresWithSkipZeroAndTopPageSize()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        var calls = new List<LoadDataArgs>();

        // Act — render a grid with LoadData + paging. Radzen raises
        // LoadData during initialization (Data is not yet provided).
        var cut = ctx.RenderComponent<RadzenDataGrid<Row>>(p => p
            .Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(this, args =>
            {
                calls.Add(args);
                return Task.CompletedTask;
            }))
            .Add(g => g.AllowPaging, true)
            .Add(g => g.PageSize, 20)
            .Add(g => g.AllowSorting, true));

        // Assert — the initial load asks for the first page.
        calls.Should().NotBeEmpty("LoadData must fire on init when Data is not set");
        calls[0].Skip.Should().Be(0, "initial load starts at row 0");
        calls[0].Top.Should().Be(20, "initial load requests PageSize rows");
        calls[0].OrderBy.Should().BeNullOrEmpty("no sort is active until the user clicks a header");
    }

    [Fact]
    public void Count_Parameter_DrivesPagerTotal_NotDataCount()
    {
        // Arrange — 45 rows total on the "server"; only the current
        // page (20 rows) is handed to the grid; Count says 45.
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        var calls = new List<LoadDataArgs>();
        var allRows = MakeRows(45);

        var cut = ctx.RenderComponent<RadzenDataGrid<Row>>(p => p
            .Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(this, args =>
            {
                calls.Add(args);
                return Task.CompletedTask;
            }))
            .Add(g => g.AllowPaging, true)
            .Add(g => g.PageSize, 20)
            .Add(g => g.ShowPagingSummary, true));

        // Act — simulate the page's LoadData handler finishing:
        // hand the grid the current page + the server-side total.
        var skip = calls[0].Skip!.Value;
        var top = calls[0].Top!.Value;
        cut.SetParametersAndRender(p => p
            .Add(g => g.Data, allRows.Skip(skip).Take(top).ToList())
            .Add(g => g.Count, allRows.Count));

        // Assert — the pager reflects the SERVER total (45): three
        // page buttons (ceil(45/20)) and a "Page 1 of 3 (45 items)"
        // summary. Not the 20-row local page. The summary format takes
        // three args: {0}=page, {1}=pages, {2}=total — pinned here so
        // the page's localized PagingSummaryFormat keeps working.
        cut.FindAll("button.rz-pager-page").Should().HaveCount(3,
            "45 server rows / 20 per page = 3 pages; the pager must use Count, not Data.Count()");
        cut.FindAll("button.rz-pager-page").Select(b => b.TextContent.Trim())
            .Should().BeEquivalentTo(new[] { "1", "2", "3" });
        cut.Markup.Should().Contain("(45 items)",
            "ShowPagingSummary must render the server-side total from Count");
    }

    [Fact]
    public async Task PagerPageClick_ReRaisesLoadDataWithCalculatedSkip()
    {
        // Arrange
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        var calls = new List<LoadDataArgs>();
        var allRows = MakeRows(45);

        var cut = ctx.RenderComponent<RadzenDataGrid<Row>>(p => p
            .Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(this, args =>
            {
                calls.Add(args);
                return Task.CompletedTask;
            }))
            .Add(g => g.AllowPaging, true)
            .Add(g => g.PageSize, 20));

        var skip = calls[0].Skip!.Value;
        var top = calls[0].Top!.Value;
        cut.SetParametersAndRender(p => p
            .Add(g => g.Data, allRows.Skip(skip).Take(top).ToList())
            .Add(g => g.Count, allRows.Count));

        // Act — click the "2" page button in the pager.
        var pageTwoButton = cut.FindAll("li a, li button")
            .FirstOrDefault(e => e.TextContent.Trim() == "2");
        pageTwoButton.Should().NotBeNull("the pager must render a page-2 button for 45 rows / 20 per page");
        await cut.InvokeAsync(() => pageTwoButton!.Click());

        // Assert — LoadData re-fires with Skip for page 2.
        calls.Count.Should().BeGreaterThanOrEqualTo(2, "clicking page 2 must re-raise LoadData");
        var last = calls[^1];
        last.Skip.Should().Be(20, "page 2 of a 20-row page size starts at row 20");
        last.Top.Should().Be(20);
    }

    [Fact]
    public async Task SortHeaderClick_ReRaisesLoadDataWithSortsAndOrderBy()
    {
        // Arrange — a grid with a sortable, filterable column. The
        // Sales page parses args.Sorts (structured) rather than the
        // args.OrderBy string; this test pins both shapes.
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        var calls = new List<LoadDataArgs>();
        var allRows = MakeRows(45);

        var cut = ctx.RenderComponent<RadzenDataGrid<Row>>(p => p
            .Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(this, args =>
            {
                calls.Add(args);
                return Task.CompletedTask;
            }))
            .Add(g => g.AllowPaging, true)
            .Add(g => g.PageSize, 20)
            .Add(g => g.AllowSorting, true)
            .Add(g => g.AllowFiltering, true)
            .Add(g => g.Columns, BuildNameColumn()));

        var skip = calls[0].Skip!.Value;
        var top = calls[0].Top!.Value;
        cut.SetParametersAndRender(p => p
            .Add(g => g.Data, allRows.Skip(skip).Take(top).ToList())
            .Add(g => g.Count, allRows.Count));

        // Act — click the column header's title element to sort
        // ascending (Radzen binds the sort toggle to the title div's
        // onclick, not the th's onmouseup).
        var titleDiv = cut.Find("th > div");
        await cut.InvokeAsync(() => titleDiv.Click());

        // Assert — LoadData re-fires carrying the sort: the OrderBy
        // string ("Name") and the structured Sorts collection with
        // Property + SortOrder.
        calls.Count.Should().BeGreaterThanOrEqualTo(2, "clicking a sortable header must re-raise LoadData");
        var last = calls[^1];
        last.OrderBy.Should().Contain(nameof(Row.Name));
        last.Sorts.Should().NotBeEmpty();
        last.Sorts!.First().Property.Should().Be(nameof(Row.Name));
        last.Sorts!.First().SortOrder.Should().Be(SortOrder.Ascending);
    }

    [Fact]
    [SuppressMessage("Usage", "BL0005:Component parameters should not be set outside their component",
        Justification = "Radzen exposes FilterValue/FilterOperator on RadzenDataGridColumn as the official programmatic filter API (radzen.com/documentation/blazor/data-grid-programmatic-filter/). Same suppression as Sales.razor.")]
    public async Task ProgrammaticFilterValuePlusReload_ReRaisesLoadDataWithFilters()
    {
        // Arrange — this is the exact pattern the Sales page's status
        // dropdown uses: set column.FilterValue programmatically, then
        // grid.Reload(). In LoadData mode the descriptor must arrive in
        // args.Filters for the server to translate.
        using var ctx = ComponentTestSetup.CreateRadzenEnabledContext();
        var calls = new List<LoadDataArgs>();
        var allRows = MakeRows(45);
        RadzenDataGrid<Row> gridRef = null!;

        var cut = ctx.RenderComponent<RadzenDataGrid<Row>>(p => p
            .Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(this, args =>
            {
                calls.Add(args);
                return Task.CompletedTask;
            }))
            .Add(g => g.AllowPaging, true)
            .Add(g => g.PageSize, 20)
            .Add(g => g.AllowFiltering, true)
            .Add(g => g.Columns, BuildNameColumn()));

        gridRef = cut.Instance;
        var skip = calls[0].Skip!.Value;
        var top = calls[0].Top!.Value;
        cut.SetParametersAndRender(p => p
            .Add(g => g.Data, allRows.Skip(skip).Take(top).ToList())
            .Add(g => g.Count, allRows.Count));
        calls.Clear();

        // Act — programmatic filter + Reload (the page's pattern).
        await cut.InvokeAsync(async () =>
        {
            var column = gridRef.ColumnsCollection.FirstOrDefault();
            column.Should().NotBeNull();
            column!.FilterValue = "Row 00";
            column.FilterOperator = FilterOperator.Contains;
            await gridRef.Reload();
        });

        // Assert — LoadData re-fires with the filter descriptor.
        calls.Should().NotBeEmpty("Reload after setting FilterValue must re-raise LoadData");
        var last = calls[^1];
        last.Filters.Should().NotBeEmpty("the column filter must arrive in args.Filters");
        var descriptor = last.Filters!.FirstOrDefault(f => f.Property == nameof(Row.Name));
        descriptor.Should().NotBeNull();
        descriptor!.FilterValue.Should().Be("Row 00");
        descriptor.FilterOperator.Should().Be(FilterOperator.Contains);
    }

    private static RenderFragment BuildNameColumn() => builder =>
    {
        builder.OpenComponent<RadzenDataGridColumn<Row>>(0);
        builder.AddAttribute(1, nameof(RadzenDataGridColumn<Row>.Property), nameof(Row.Name));
        builder.AddAttribute(2, nameof(RadzenDataGridColumn<Row>.Title), "Name");
        builder.CloseComponent();
    };
}

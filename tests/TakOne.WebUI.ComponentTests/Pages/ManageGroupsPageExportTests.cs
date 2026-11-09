using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Models;
using TakOne.Application.Customers.DTOs;
using TakOne.Application.Customers.Queries.GetAllCustomerGroups;
using TakOne.Application.Resources;
using TakOne.Application.Settings.DTOs;
using TakOne.Application.Settings.Queries.GetSystemSettings;
using TakOne.Domain.Common.Enums;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.DTOs;
using TakOne.WebUI.Components.Pages.ManageGroups;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit test for the ManageGroups CSV export (the last list
/// page to gain an export). Mirrors <see cref="AdminUsersPageLoadDataTests"/>
/// in wiring, but asserts the EXPORT flow instead of grid LoadData.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>: the export button renders with a label (the
/// Task-2 empty-button class of bug — ChildContent overriding Text —
/// can NEVER regress here silently), it is disabled while the list is
/// empty, and a click re-queries <c>GetAllCustomerGroupsQuery</c>, then
/// hands a correctly-shaped CSV (localized headers, RFC 4180 rows,
/// invariant RAW salary amounts) + a <c>takone-groups-*</c> file name to
/// the <c>takDownload.csv</c> JS helper.
/// </para>
/// <para>
/// <b>JS-INTEROP ASSERTION NOTE</b>: the context runs in Loose mode
/// (Radzen components fire unplanned registration calls like
/// <c>Radzen.createDataGrid</c> during render). In Loose mode bUnit
/// records EVERY invocation in the context-wide
/// <c>JSInterop.Invocations</c> dictionary (keyed by identifier) — the
/// export's <c>takDownload.csv</c> call is therefore asserted via that
/// global dictionary, NOT via a <c>SetupVoid</c> handler's own list
/// (which only records argument-matched planned invocations).
/// </para>
/// </remarks>
public class ManageGroupsPageExportTests
{
    private static List<CustomerGroupListItemDto> MakeGroups(int count) =>
        Enumerable.Range(1, count).Select(i => new CustomerGroupListItemDto
        {
            Id = Guid.NewGuid(),
            Name = i == 1 ? "Group, \"VIP\"" : $"Group {i:00}", // one name with CSV-hostile chars
            Salary = new MoneyDto { Amount = 1500.5m * i, Currency = "IRR" },
            IsActive = i % 2 == 1
        }).ToList();

    private static (BunitContext ctx, IMessageBus bus, BunitJSInterop js) Setup(List<CustomerGroupListItemDto> groups)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizer for the page's own Loc[...] lookups — also
        // stub the format-args indexer (the export-done toast uses
        // Loc["ExportCsvDone", count]).
        var loc = Substitute.For<IStringLocalizer<ManageGroups>>();
        loc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        loc[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(loc);

        // ErrorDisplayService + ToastService (same real/stub mix as the
        // AdminUsers page tests).
        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        // ManageGroups injects ILogger (dialog-open failure path) + the
        // Radzen DialogService (deactivate confirm — not exercised here).
        ctx.Services.AddSingleton(Substitute.For<ILogger<ManageGroups>>());
        ComponentTestSetup.AddDialogServiceSpy(ctx);

        // Admin user: the page then also loads the limit-mode card.
        ComponentTestSetup.AddAuthenticatedUser(ctx, "tester", Roles.Admin);

        // The fake bus: groups list (initial load + the export's re-query)
        // + the limit-mode settings.
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<Result<List<CustomerGroupListItemDto>>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<CustomerGroupListItemDto>>.Success(groups));
        bus.InvokeAsync<Result<SystemSettingsDto>>(Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Result<SystemSettingsDto>.Success(new SystemSettingsDto
            {
                LimitMode = LimitMode.CountOnly,
                UpdatedAtUtc = DateTime.UtcNow
            }));
        ctx.Services.AddSingleton(bus);

        return (ctx, bus, ctx.JSInterop);
    }

    [Fact]
    public async Task ManageGroups_ExportClick_DownloadsCsvWithAllGroups()
    {
        // Arrange — 3 groups (one with CSV-hostile punctuation in the name).
        var groups = MakeGroups(3);
        var (ctx, bus, js) = Setup(groups);
        using (ctx)
        {
            var cut = ctx.Render<ManageGroups>();
            cut.WaitForAssertion(
                () => cut.FindAll(".group-name-cell").Should().HaveCount(3),
                TimeSpan.FromSeconds(10));

            // The export button renders WITH its label (guards the
            // empty-button regression class) and is enabled for a
            // non-empty list.
            var exportButton = cut.FindAll("button")
                .First(b => b.TextContent.Contains("ExportCsvButton"));
            exportButton.HasAttribute("disabled").Should().BeFalse();

            // Act — click export.
            await cut.InvokeAsync(() => exportButton.Click());

            // Assert — the takDownload.csv JS call happened with the
            // right shape (see the class remarks for why the global
            // Loose-mode dictionary is the assertion surface).
            cut.WaitForAssertion(
                () => js.Invocations["takDownload.csv"].Should().HaveCount(1),
                TimeSpan.FromSeconds(10));

            var args = js.Invocations["takDownload.csv"][0].Arguments;
            args.Should().HaveCount(2);
            args[0].Should().BeOfType<string>().Which.Should().StartWith("takone-groups-").And.EndWith(".csv");

            // The CSV carries the 4 localized header columns + all 3 rows
            // (CRLF records; the punctuation-laden name is quoted per
            // RFC 4180; RAW invariant amounts, no grouping).
            var csv = args[1].Should().BeOfType<string>().Subject;
            csv.Should().StartWith("ColName,ColSalary,ColCurrency,ColStatus\r\n");
            csv.Should().Contain("\"Group, \"\"VIP\"\"\",1500.5,IRR,StatusActiveBadge\r\n");
            csv.Should().Contain("Group 02,3001,IRR,StatusInactiveBadge\r\n");
            csv.Should().Contain("Group 03,4501.5,IRR,StatusActiveBadge\r\n");
            csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4);

            // The export re-queried (initial load + export = 2 dispatches).
            await bus.Received(2).InvokeAsync<Result<List<CustomerGroupListItemDto>>>(
                Arg.Any<GetAllCustomerGroupsQuery>(), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public void ManageGroups_EmptyGroupList_ExportButtonIsDisabled()
    {
        // Arrange — fresh install: no groups at all.
        var (ctx, _, _) = Setup(new List<CustomerGroupListItemDto>());
        using (ctx)
        {
            // Act
            var cut = ctx.Render<ManageGroups>();

            // Assert — the fresh-install empty card renders and the
            // export button is disabled (belt + braces: the click
            // handler also no-ops, but a disabled control can't be
            // clicked at all).
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("EmptyTitle"),
                TimeSpan.FromSeconds(10));

            var exportButton = cut.FindAll("button")
                .First(b => b.TextContent.Contains("ExportCsvButton"));
            exportButton.HasAttribute("disabled").Should().BeTrue();
        }
    }
}

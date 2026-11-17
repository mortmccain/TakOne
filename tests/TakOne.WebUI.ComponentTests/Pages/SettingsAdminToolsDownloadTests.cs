using System.Security.Claims;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Common.Models;
using TakOne.Application.Notifications.DTOs;
using TakOne.Application.Notifications.Queries.GetNotificationPreferences;
using TakOne.Application.Resources;
using TakOne.Application.Settings.Queries.GetBackupStatus;
using TakOne.Application.Users.DTOs;
using TakOne.Application.Users.Queries.GetUserById;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;
using TakOne.WebUI.Components.Pages.AdminProducts;
using TakOne.WebUI.Components.Pages.AdminUsers;
using TakOne.WebUI.Components.Pages.AdminCategories;
using TakOne.WebUI.Components.Pages.Settings;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit tests for the Settings admin-tools cards' CSV
/// TEMPLATE downloads — the last untested interactive surface of the
/// import feature (QA-2).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>: clicking each card's "Download template"
/// button produces a <c>takDownload.csv</c> JS invocation whose content is
/// the EXACT positional format the corresponding import expects — the
/// product template's 10 columns (incl. the two picture columns) match
/// the AdminProducts export's headers/order, and the user template's 6
/// columns match the manage-users export's. A drift between template and
/// import parser would break round-trips; these tests pin the contract.
/// </para>
/// <para>
/// <b>WIRING</b>: same full-page mount as
/// <see cref="SettingsBackupCardTests"/> — the three identity localizers
/// (page + AdminProducts + AdminUsers) with the format-args indexer
/// stubbed, the fake bus answering the three mount queries, an Admin
/// auth state with the UserId claim, and IFileStorage.
/// </para>
/// <para>
/// The import-REPORT downloads (per-run ledgers) render only after an
/// actual upload/import round-trip through RadzenInputFile — out of
/// page-test scope; their builders mirror the template handlers tested
/// here (same CsvBuilder + takDownload.csv pipeline, also covered by
/// the run-report download test in SettingsBackupCardTests).
/// </para>
/// </remarks>
public class SettingsAdminToolsDownloadTests
{
    private static (BunitContext ctx, BunitJSInterop js) Setup()
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizers for the page's three injected localizers —
        // the format-args indexer too (Settings formats several values).
        foreach (var _ in new[] { 1 }) // keep the registrar block a single statement
        {
            AddLoc(ctx);
        }

        var errLoc = Substitute.For<IStringLocalizer<UnexpectedErrorMessages>>();
        errLoc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(errLoc);
        ctx.Services.AddSingleton(new ErrorDisplayService(errLoc));
        ctx.Services.AddSingleton(new Radzen.NotificationService());
        ctx.Services.AddSingleton(sp => new ToastService(
            sp.GetRequiredService<Radzen.NotificationService>(),
            sp.GetRequiredService<ErrorDisplayService>()));

        // Admin WITH the "UserId" claim the page requires (its IdClaim
        // lookup) — mirrors SettingsBackupCardTests.
        var userId = Guid.NewGuid();
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "admin-user"),
            new("UserId", userId.ToString()),
            new(ClaimTypes.Role, Roles.Admin)
        };
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, "TestAuthentication"));
        var authProvider = Substitute.For<AuthenticationStateProvider>();
        authProvider.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(new AuthenticationState(principal)));
        ctx.Services.AddSingleton(authProvider);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(authProvider);

        // The fake bus: the three queries the page dispatches on mount.
        var bus = Substitute.For<IMessageBus>();
        bus.InvokeAsync<Result<UserDto>>(Arg.Any<GetUserByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<UserDto>.Success(new UserDto
            {
                Id = userId,
                WorkerId = "0001",
                FullName = "Admin User"
            }));
        bus.InvokeAsync<Result<IReadOnlyList<NotificationPreferenceDto>>>(
                Arg.Any<GetNotificationPreferencesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<NotificationPreferenceDto>>.Success(
                new List<NotificationPreferenceDto>()));
        bus.InvokeAsync<Result<BackupStatusDto>>(
                Arg.Any<GetBackupStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<BackupStatusDto>.Success(new BackupStatusDto
            {
                Enabled = false // the admin-tools cards render regardless
            }));
        ctx.Services.AddSingleton(bus);

        ctx.Services.AddSingleton(Substitute.For<IFileStorage>());

        return (ctx, ctx.JSInterop);
    }

    private static void AddLoc(BunitContext ctx)
    {
        AddIdentityLocalizer<Settings>(ctx);
        AddIdentityLocalizer<AdminProducts>(ctx);
        AddIdentityLocalizer<AdminUsers>(ctx);
        AddIdentityLocalizer<AdminCategories>(ctx);
    }

    // Local twin of ComponentTestSetup.AddIdentityLocalizer that ALSO
    // stubs the format-args indexer (same shape as SettingsBackupCardTests).
    private static void AddIdentityLocalizer<T>(BunitContext ctx) where T : class
    {
        var loc = Substitute.For<IStringLocalizer<T>>();
        loc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        loc[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(loc);
    }

    [Fact]
    public async Task Settings_ProductTemplateClick_DownloadsTenColumnCsv()
    {
        // Arrange
        var (ctx, js) = Setup();
        using (ctx)
        {
            var cut = ctx.Render<Settings>();
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("ProductImportCardTitle"),
                TimeSpan.FromSeconds(10));

            // Both cards carry a template button with the SAME label —
            // the products one comes first in the DOM.
            var templateButtons = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("DownloadTemplateButton"))
                .ToList();
            templateButtons.Should().HaveCountGreaterThanOrEqualTo(2);
            var productTemplateButton = templateButtons.First();

            // Act
            await cut.InvokeAsync(() => productTemplateButton.Click());

            // Assert — the takDownload.csv JS call (global Loose-mode
            // dictionary; see ManageGroupsPageExportTests' remarks).
            cut.WaitForAssertion(
                () => js.Invocations["takDownload.csv"].Should().HaveCount(1),
                TimeSpan.FromSeconds(10));

            var args = js.Invocations["takDownload.csv"][0].Arguments;
            args.Should().HaveCount(2);
            args[0].Should().BeOfType<string>().Which.Should().Be("takone-products-import-template.csv");

            // The EXACT positional 10-column format (identity localizer:
            // header keys render verbatim), incl. BOTH picture columns,
            // and the two example rows (one with, one without a picture).
            var csv = args[1].Should().BeOfType<string>().Subject;
            csv.Should().StartWith(
                "ColName,ColCategory,ColSubCategory,ColSubSubCategory," +
                "ColPrice,ColCurrency,ColStock,ColStatus," +
                "ColPictureDirectory,ColPictureFileName\r\n");
            csv.Should().Contain(
                "Example Product A,Example Category,Example Sub-Category,Example Sub-Sub-Category," +
                "100.5,IRR,10,StatusInStockBadge,uploads/products,example-product-a.jpg\r\n");
            csv.Should().Contain(
                "Example Product B,Example Category,Example Sub-Category,," +
                "250,IRR,0,StatusOutOfStockBadge,,\r\n");
            csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task Settings_UserTemplateClick_DownloadsSixColumnCsv()
    {
        // Arrange
        var (ctx, js) = Setup();
        using (ctx)
        {
            var cut = ctx.Render<Settings>();
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("ProductImportCardTitle"),
                TimeSpan.FromSeconds(10));

            var templateButtons = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("DownloadTemplateButton"))
                .ToList();
            // Card order: products (1), users (3), categories (4) — each
            // card's template button, in markup order.
            templateButtons.Should().HaveCount(3);
            var userTemplateButton = templateButtons[1]; // the users card's

            // Act
            await cut.InvokeAsync(() => userTemplateButton.Click());

            // Assert
            cut.WaitForAssertion(
                () => js.Invocations["takDownload.csv"].Should().HaveCount(1),
                TimeSpan.FromSeconds(10));

            var args = js.Invocations["takDownload.csv"][0].Arguments;
            args[0].Should().BeOfType<string>().Which.Should().Be("takone-users-import-template.csv");

            // The EXACT positional 6-column format (worker id, full name,
            // gender, group, roles, status) + the two example rows. The
            // roles cells are the SEEDED ENGLISH role names (the import
            // validates them Ordinal-exact) — pinned deliberately.
            var csv = args[1].Should().BeOfType<string>().Subject;
            csv.Should().StartWith(
                "ColWorkerId,ColFullName,ColGender,ColGroup,ColRoles,ColStatus\r\n");
            csv.Should().Contain("10001,Example Employee,GenderMale,,Employee,StatusActive\r\n");
            csv.Should().Contain("10002,Example Customer,GenderFemale,Example Group,Customer,StatusActive\r\n");
            csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task Settings_CategoriesTemplateClick_DownloadsFourColumnCsv()
    {
        // The categories-import card's template: the EXACT positional
        // 4-column format of the AdminCategories export (Category,
        // Sub-category, Sub-sub-category, Status) + three example rows —
        // one per chain depth (category only, category+sub, full triple).
        var (ctx, js) = Setup();
        using (ctx)
        {
            var cut = ctx.Render<Settings>();
            cut.WaitForAssertion(
                () => cut.Markup.Should().Contain("ProductImportCardTitle"),
                TimeSpan.FromSeconds(10));

            var templateButtons = cut.FindAll("button")
                .Where(b => b.TextContent.Contains("DownloadTemplateButton"))
                .ToList();
            templateButtons.Should().HaveCount(3);
            var categoriesTemplateButton = templateButtons[2]; // the categories card's

            // Act
            await cut.InvokeAsync(() => categoriesTemplateButton.Click());

            // Assert
            cut.WaitForAssertion(
                () => js.Invocations["takDownload.csv"].Should().HaveCount(1),
                TimeSpan.FromSeconds(10));

            var args = js.Invocations["takDownload.csv"][0].Arguments;
            args[0].Should().BeOfType<string>().Which.Should().Be("takone-categories-import-template.csv");

            var csv = args[1].Should().BeOfType<string>().Subject;
            csv.Should().StartWith("ColCategory,ColSubCategory,ColSubSubCategory,ColStatus\r\n");
            csv.Should().Contain("Example Category,,,StatusActive\r\n");
            csv.Should().Contain("Example Category,Example Sub-category,,StatusActive\r\n");
            csv.Should().Contain(
                "Example Category,Example Sub-category,Example Sub-sub-category,StatusActive\r\n");
            csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4);
        }
    }
}

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
using TakOne.WebUI.Components.Pages.Settings;
using TakOne.WebUI.Services;
using Wolverine;
using Xunit;

namespace TakOne.WebUI.ComponentTests.Pages;

/// <summary>
/// Page-level bUnit test for the Settings admin backup card's run-history
/// ledger (the persisted JSONL audit trail surfaced by GetBackupStatus).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHAT THIS PROVES</b>: the admin-only card renders the
/// <c>backup-run-list</c> ledger from <see cref="BackupStatusDto.RunHistory"/>
/// — one row per completed run with its file name, the failure row keeps
/// the <c>backup-run-failed</c> modifier class, the last-failure alert
/// shows the error message, and the section stays HIDDEN when no run has
/// completed (fresh-install card shows stats only).
/// </para>
/// <para>
/// <b>WIRING</b>: the full Settings page is mounted with its real
/// dependencies (ToastService, ErrorDisplayService) and NSubstitute fakes
/// for the message bus (user + notification-preferences + backup-status
/// queries), the auth state (Admin role + a "UserId" claim — see the
/// page's OnInitializedAsync), and <c>IFileStorage</c>. The JS runtime is
/// bUnit's Loose-mode mock — the appearance card's <c>takTheme.get</c>
/// call returns default and the page handles it.
/// </para>
/// </remarks>
public class SettingsBackupCardTests
{
    private static BackupStatusDto MakeStatus(params BackupRunRecordDto[] history) => new()
    {
        Enabled = true,
        IsRunning = false,
        LastRunStartedUtc = history.Length > 0 ? history[0].StartedUtc : null,
        LastRunFinishedUtc = history.Length > 0 ? history[0].FinishedUtc : null,
        LastRunSucceeded = history.Length > 0 ? history[0].Succeeded : null,
        LastRunError = history.Length > 0 ? history[0].Error : string.Empty,
        NextScheduledRunUtc = DateTime.UtcNow.AddHours(3),
        RetentionDays = 7,
        Files = new List<BackupFileInfoDto>(),
        TotalSizeBytes = 0,
        RunHistory = history.ToList()
    };

    private static BackupRunRecordDto MakeRun(
        bool succeeded,
        bool wasManual,
        string fileName,
        string error = "") => new()
    {
        StartedUtc = new DateTime(2026, 10, 3, 3, 30, 0, DateTimeKind.Utc),
        FinishedUtc = new DateTime(2026, 10, 3, 3, 30, 12, DateTimeKind.Utc),
        Succeeded = succeeded,
        Error = error,
        FileName = fileName,
        SizeBytes = succeeded ? 2048 : 0,
        WasManual = wasManual
    };

    private static TestContext Setup(BackupStatusDto backupStatus)
    {
        var ctx = ComponentTestSetup.CreateRadzenEnabledContext();

        // Identity localizers for the page's three injected localizers —
        // the format-args indexer too (several Settings strings format
        // values, e.g. BackupFilesSummary).
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

        // Auth state: an Admin WITH the "UserId" claim the page requires
        // (its IdClaim lookup) — the shared helper only adds name+roles.
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
            .Returns(Result<BackupStatusDto>.Success(backupStatus));
        ctx.Services.AddSingleton(bus);

        // IFileStorage is injected by the page (bulk-picture upload card).
        ctx.Services.AddSingleton(Substitute.For<IFileStorage>());

        return ctx;
    }

    private static void AddLoc(TestContext ctx)
    {
        AddIdentityLocalizer<Settings>(ctx);
        AddIdentityLocalizer<AdminProducts>(ctx);
        AddIdentityLocalizer<AdminUsers>(ctx);
    }

    // Local twin of ComponentTestSetup.AddIdentityLocalizer that ALSO
    // stubs the format-args indexer (Settings uses Loc["Key", args]
    // for BackupFilesSummary / BackupRetentionValue / duration lines).
    private static void AddIdentityLocalizer<T>(TestContext ctx) where T : class
    {
        var loc = Substitute.For<IStringLocalizer<T>>();
        loc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        loc[Arg.Any<string>(), Arg.Any<object[]>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), false));
        ctx.Services.AddSingleton(loc);
    }

    [Fact]
    public void Settings_BackupCard_RendersRunHistoryLedgerWithOutcomeRows()
    {
        // Arrange — one successful MANUAL run + one FAILED SCHEDULED run.
        var status = MakeStatus(
            MakeRun(succeeded: false, wasManual: false, fileName: "",
                error: "file was not found at /var/opt/mssql/backup after the run"),
            MakeRun(succeeded: true, wasManual: true, fileName: "TakOne-backup-20261003-033000.bak"));
        using var ctx = Setup(status);

        // Act — mount the full Settings page as the admin.
        var cut = ctx.RenderComponent<Settings>();

        // Assert — the run-history ledger renders one row per record,
        // newest first: the failed scheduled run first (danger-tinted),
        // then the successful manual run.
        cut.WaitForAssertion(
            () => cut.FindAll(".backup-run-row").Should().HaveCount(2),
            TimeSpan.FromSeconds(10));

        var rows = cut.FindAll(".backup-run-row");
        rows[0].ClassList.Should().Contain("backup-run-failed",
            "the failed run's row keeps the danger modifier class");
        rows[0].GetAttribute("title").Should().Contain("file was not found",
            "the failed run's full error rides the row's title tooltip");

        rows[1].ClassList.Should().NotContain("backup-run-failed");
        rows[1].TextContent.Should().Contain("TakOne-backup-20261003-033000.bak");
        // The manual run shows the touch_app (hand) icon, the scheduled
        // run the schedule (clock) icon.
        rows[0].InnerHtml.Should().Contain("schedule");
        rows[1].InnerHtml.Should().Contain("touch_app");

        // The last-failure alert (a SEPARATE surface from the ledger)
        // shows the same error the stats grid reports.
        cut.Markup.Should().Contain("BackupFailedAlertTitle");
        cut.Markup.Should().Contain("file was not found at /var/opt/mssql/backup after the run");

        // The ledger has its section label; both ledgers are present.
        cut.Markup.Should().Contain("BackupRunHistoryLabel");
        cut.FindAll(".backup-run-list").Should().HaveCount(1);
    }

    [Fact]
    public void Settings_BackupCard_NoCompletedRuns_HidesRunHistorySection()
    {
        // Arrange — armed service, never ran: stats only, no ledger.
        var status = MakeStatus();
        using var ctx = Setup(status);

        // Act
        var cut = ctx.RenderComponent<Settings>();

        // Assert — the card renders its health stats (the "never run"
        // hint) but the run-history section is hidden entirely — the
        // stats grid's "Last run: never" already covers that case.
        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("BackupCardTitle"),
            TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("BackupNeverRun"),
            TimeSpan.FromSeconds(10));

        cut.FindAll(".backup-run-row").Should().BeEmpty();
        cut.Markup.Should().NotContain("BackupRunHistoryLabel");
    }

    [Fact]
    public async Task Settings_BackupCard_RunReportClick_DownloadsChronologicalCsv()
    {
        // Arrange — newest-first history (the DTO contract): a FAILED
        // SCHEDULED run (newer) + a SUCCESSFUL MANUAL run (older). The
        // report must reverse that into execution order.
        var older = new BackupRunRecordDto
        {
            StartedUtc = new DateTime(2026, 10, 2, 3, 30, 0, DateTimeKind.Utc),
            FinishedUtc = new DateTime(2026, 10, 2, 3, 30, 12, 500, DateTimeKind.Utc),
            Succeeded = true,
            Error = string.Empty,
            FileName = "run-a.bak",
            SizeBytes = 2048,
            WasManual = true
        };
        var newer = new BackupRunRecordDto
        {
            StartedUtc = new DateTime(2026, 10, 3, 14, 30, 0, DateTimeKind.Utc),
            FinishedUtc = new DateTime(2026, 10, 3, 14, 30, 9, DateTimeKind.Utc),
            Succeeded = false,
            // CSV-hostile punctuation pins the RFC 4180 quoting.
            Error = "boom, \"volume\" not mounted",
            FileName = string.Empty,
            SizeBytes = 0,
            WasManual = false
        };
        var status = MakeStatus(newer, older);
        using var ctx = Setup(status);
        var js = ctx.JSInterop;

        var cut = ctx.RenderComponent<Settings>();
        cut.WaitForAssertion(
            () => cut.FindAll(".backup-run-row").Should().HaveCount(2),
            TimeSpan.FromSeconds(10));

        // The run-report button is present with its label and enabled.
        var reportButton = cut.FindAll("button")
            .First(b => b.TextContent.Contains("BackupRunReportButton"));
        reportButton.HasAttribute("disabled").Should().BeFalse();

        // Act — click download.
        await cut.InvokeAsync(() => reportButton.Click());

        // Assert — one takDownload.csv invocation (global Loose-mode
        // dictionary; see ManageGroupsPageExportTests' class remarks).
        cut.WaitForAssertion(
            () => js.Invocations["takDownload.csv"].Should().HaveCount(1),
            TimeSpan.FromSeconds(10));

        var args = js.Invocations["takDownload.csv"][0].Arguments;
        args.Should().HaveCount(2);
        args[0].Should().BeOfType<string>().Which
            .Should().StartWith("takone-backup-runs-").And.EndWith(".csv");

        var csv = args[1].Should().BeOfType<string>().Subject;
        csv.Should().StartWith(
            "BackupRunReportColStarted,BackupRunReportColFinished," +
            "BackupRunReportColDuration,BackupRunReportColOutcome," +
            "BackupRunReportColTrigger,BackupRunReportColFile," +
            "BackupRunReportColSize,BackupRunReportColError\r\n");

        // CHRONOLOGICAL order: the older SUCCESS row first (ISO-8601 UTC
        // timestamps, raw duration seconds incl. the half-second, raw
        // bytes, localized outcome/trigger labels = keys in the stub).
        csv.Should().Contain(
            "2026-10-02 03:30:00Z,2026-10-02 03:30:12Z,12.5," +
            "BackupOutcomeSuccess,BackupRunManual,run-a.bak,2048,\r\n");

        // Then the newer FAILED row: empty file + 0 bytes, and the
        // punctuation-laden error quoted per RFC 4180.
        csv.Should().Contain(
            "2026-10-03 14:30:00Z,2026-10-03 14:30:09Z,9," +
            "BackupOutcomeFailed,BackupRunScheduled,,0," +
            "\"boom, \"\"volume\"\" not mounted\"\r\n");

        // Header + 2 rows = 3 records.
        csv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3);
    }

    [Fact]
    public void Settings_BackupCard_NoCompletedRuns_RunReportButtonDisabled()
    {
        // Arrange — never ran: nothing to report.
        var status = MakeStatus();
        using var ctx = Setup(status);

        var cut = ctx.RenderComponent<Settings>();
        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("BackupCardTitle"),
            TimeSpan.FromSeconds(10));

        // Assert — the button renders (consistent action row) but is
        // disabled until at least one run has completed.
        var reportButton = cut.FindAll("button")
            .First(b => b.TextContent.Contains("BackupRunReportButton"));
        reportButton.HasAttribute("disabled").Should().BeTrue();
    }
}

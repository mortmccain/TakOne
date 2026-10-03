using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Common.Models;
using TakOne.Infrastructure.Services;
using Xunit;

namespace TakOne.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for the run-history ledger of
/// <see cref="DatabaseBackupHostedService"/> (the JSONL audit log next to
/// the <c>*.bak</c> files).
///
/// COVERAGE APPROACH:
///   The ledger's WRITE path executes on the schedule loop with a live
///   SQL Server — not unit-testable here (see the project's SCOPE remark).
///   What IS testable without any database is everything the
///   <see cref="IBackupCoordinator"/> surface reads/writes around the
///   ledger file:
///     • GetStatus parses the JSONL into RunHistory — newest first
///     • GetStatus tolerates a MISSING ledger (fresh install)
///     • GetStatus tolerates a TORN/partial tail line (a concurrent
///       append) and legacy junk lines — they are skipped, never thrown
///     • GetStatus CAPS the returned history (newest kept)
///     • TryTriggerNow is rejected when the service is not armed
///       (disabled configuration), and accepted when armed — the armed
///       state is reached by letting StartAsync/StopAsync resolve the
///       configuration once (the loop then idles until the next UTC slot,
///       so no backup actually executes during the test).
/// </summary>
public class DatabaseBackupHostedServiceHistoryTests : IDisposable
{
    private readonly string _tempDir;

    public DatabaseBackupHostedServiceHistoryTests()
    {
        // A fresh temp directory per test — the ledger's directory is
        // resolved from configuration, so each test gets a clean slate.
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "takone-backup-history-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup — a locked file doesn't fail the test.
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds the service with an ARMED configuration (enabled + a
    /// connection string + the test's temp directory) and runs one
    /// Start/Stop cycle so ExecuteAsync resolves + caches the config.
    /// The next scheduled slot is at most ~11h in the future, so the
    /// loop only IDLES while armed — no backup executes against the
    /// fake connection string.
    /// </summary>
    /// <remarks>
    /// <b>WHY THE ARMED POLL</b>: .NET's <c>BackgroundService.StartAsync</c>
    /// dispatches <c>ExecuteAsync</c> onto the thread pool — the
    /// synchronous config block that arms the coordinator (sets
    /// <c>_armed</c> / <c>_directory</c> / <c>_retentionDays</c>) can
    /// complete AFTER <c>StartAsync</c> returns. Polling the observable
    /// state (retention = the non-default test value ⇒ the config block
    /// ran) makes the helper deterministic regardless of dispatch
    /// timing.
    /// </remarks>
    private static async Task<DatabaseBackupHostedService> CreateArmedServiceAsync(
        string directory,
        int retentionDays = 9)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseBackups:Enabled"] = "true",
                ["DatabaseBackups:RetentionDays"] = retentionDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["DatabaseBackups:Directory"] = directory,
                // Unreachable server — even in the (astronomically
                // unlikely) event a slot fires mid-test, the run fails
                // fast and harmlessly.
                ["TakOne:Database:ConnectionString"] =
                    "Server=takone-history-tests-unreachable;Database=TakOne;TrustServerCertificate=True"
            })
            .Build();

        var service = new DatabaseBackupHostedService(
            config,
            NullLogger<DatabaseBackupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Wait until the dispatched ExecuteAsync has resolved the
        // config (observable: RetentionDays flips from the 7-day default
        // to the test's 9). Bounded so a broken config can't hang the
        // suite; 5s is ~250× longer than the dispatch takes in practice.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.GetStatus().RetentionDays != retentionDays
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await service.StopAsync(CancellationToken.None);

        return service;
    }

    /// <summary>Writes the supplied lines verbatim as the ledger file.</summary>
    private void WriteLedger(params string[] lines)
    {
        File.WriteAllText(
            Path.Combine(_tempDir, "backup-history.jsonl"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>One valid ledger line for the given run.</summary>
    private static string LedgerLine(
        DateTime finishedUtc,
        bool succeeded,
        bool wasManual,
        string fileName = "TakOne-backup-20260101-033000.bak",
        long sizeBytes = 1024,
        string error = "")
        => $$"""
             {"StartedUtc":"{{finishedUtc.AddSeconds(-30):yyyy-MM-ddTHH:mm:ssZ}}","FinishedUtc":"{{finishedUtc:yyyy-MM-ddTHH:mm:ssZ}}","Succeeded":{{(succeeded ? "true" : "false")}},"Error":"{{error}}","FileName":"{{fileName}}","SizeBytes":{{sizeBytes}},"WasManual":{{(wasManual ? "true" : "false")}}}
             """;

    // ── GetStatus → RunHistory ────────────────────────────────────────

    [Fact]
    public async Task GetStatus_WithLedger_ReturnsRunHistoryNewestFirst()
    {
        // Arrange — two records on disk, OLDEST first (append order).
        var older = new DateTime(2026, 1, 5, 3, 30, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 1, 5, 14, 30, 0, DateTimeKind.Utc);
        WriteLedger(
            LedgerLine(older, succeeded: true, wasManual: false, fileName: "a.bak", sizeBytes: 10),
            LedgerLine(newer, succeeded: true, wasManual: true, fileName: "b.bak", sizeBytes: 20));

        var service = await CreateArmedServiceAsync(_tempDir);

        // Act
        var status = service.GetStatus();

        // Assert — newest first, both records, fields round-tripped.
        status.RunHistory.Should().HaveCount(2);
        status.RunHistory[0].FileName.Should().Be("b.bak");
        status.RunHistory[0].WasManual.Should().BeTrue();
        status.RunHistory[0].SizeBytes.Should().Be(20);
        status.RunHistory[0].Succeeded.Should().BeTrue();
        status.RunHistory[1].FileName.Should().Be("a.bak");
        status.RunHistory[1].WasManual.Should().BeFalse();
        status.RunHistory[1].StartedUtc.Should().Be(older.AddSeconds(-30));
        status.RunHistory[1].FinishedUtc.Should().Be(older);
    }

    [Fact]
    public async Task GetStatus_WithoutLedger_ReturnsEmptyHistory()
    {
        // Arrange — armed service, empty backup directory (fresh install
        // that has never completed a run).
        var service = await CreateArmedServiceAsync(_tempDir);

        // Act
        var status = service.GetStatus();

        // Assert
        status.RunHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatus_WithTornTailLine_SkipsItAndKeepsWholeLines()
    {
        // Arrange — a fully-written record, a junk line, and a TORN tail
        // (what a reader sees while the loop is mid-append).
        var whole = new DateTime(2026, 1, 6, 3, 30, 0, DateTimeKind.Utc);
        WriteLedger(
            LedgerLine(whole, succeeded: true, wasManual: false),
            "this is not json",
            "{\"StartedUtc\":\"2026-01-06T14:30:00Z\",\"FinishedUtc\":\"2026-01-06T14:30");

        var service = await CreateArmedServiceAsync(_tempDir);

        // Act — must NOT throw; only the parseable line survives.
        var status = service.GetStatus();

        // Assert
        status.RunHistory.Should().ContainSingle();
        status.RunHistory[0].FinishedUtc.Should().Be(whole);
    }

    [Fact]
    public async Task GetStatus_WithManyRecords_CapsAtFiftyNewest()
    {
        // Arrange — 60 records, one per hour, oldest first.
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        WriteLedger(Enumerable.Range(0, 60)
            .Select(i => LedgerLine(baseTime.AddHours(i), succeeded: true, wasManual: false, fileName: $"run-{i:00}.bak"))
            .ToArray());

        var service = await CreateArmedServiceAsync(_tempDir);

        // Act
        var status = service.GetStatus();

        // Assert — 50 records, the NEWEST 50 (run-59 … run-10).
        status.RunHistory.Should().HaveCount(50);
        status.RunHistory[0].FileName.Should().Be("run-59.bak");
        status.RunHistory[^1].FileName.Should().Be("run-10.bak");
    }

    // ── Armed / not-armed trigger gating ─────────────────────────────

    [Fact]
    public async Task TryTriggerNow_WhenArmed_IsAcceptedOnceThenRejectedWhilePending()
    {
        var service = await CreateArmedServiceAsync(_tempDir);

        // Act — first click accepted; the second is rejected while the
        // trigger is pending (the loop is stopped in this test, so the
        // pending flag is never consumed).
        var first = service.TryTriggerNow();
        var second = service.TryTriggerNow();

        // Assert — per-reason contract: Accepted, then RejectedPending.
        first.Should().Be(BackupTriggerOutcome.Accepted);
        second.Should().Be(BackupTriggerOutcome.RejectedPending);
    }

    [Fact]
    public async Task TryTriggerNow_WhenDisabled_IsRejected()
    {
        // Arrange — Enabled=false: StartAsync returns without arming.
        // RetentionDays=9 (non-default) so the poll below can observe
        // that the dispatched ExecuteAsync DID process the config.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseBackups:Enabled"] = "false",
                ["DatabaseBackups:RetentionDays"] = "9",
                ["DatabaseBackups:Directory"] = _tempDir,
                ["TakOne:Database:ConnectionString"] = "Server=x;Database=TakOne"
            })
            .Build();

        var service = new DatabaseBackupHostedService(
            config,
            NullLogger<DatabaseBackupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        // Wait until the dispatched ExecuteAsync processed the config
        // (observable: RetentionDays flips from the 7-day default to
        // the test's 9) — see CreateArmedServiceAsync's remarks on
        // .NET's thread-pool dispatch of ExecuteAsync.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.GetStatus().RetentionDays != 9
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await service.StopAsync(CancellationToken.None);

        // Act + Assert — not armed (per-reason contract: RejectedDisabled);
        // the status reports disabled too.
        service.TryTriggerNow().Should().Be(BackupTriggerOutcome.RejectedDisabled);
        service.GetStatus().Enabled.Should().BeFalse();
        service.GetStatus().RunHistory.Should().BeEmpty();
    }

    // ── Status shape sanity (DTO contract used by the Settings card) ──

    [Fact]
    public async Task GetStatus_FailedRunWithError_SurfacesErrorInHistory()
    {
        // Arrange — a failed scheduled run with the wiring-hint message.
        var failedAt = new DateTime(2026, 1, 7, 3, 30, 0, DateTimeKind.Utc);
        WriteLedger(LedgerLine(
            failedAt,
            succeeded: false,
            wasManual: false,
            fileName: "",
            sizeBytes: 0,
            error: "BACKUP DATABASE reported success, but the file was not found."));

        var service = await CreateArmedServiceAsync(_tempDir);

        // Act
        var status = service.GetStatus();

        // Assert — the record keeps the failure detail for the card's
        // tooltip and the outcome is false.
        status.RunHistory.Should().ContainSingle();
        var run = status.RunHistory[0];
        run.Succeeded.Should().BeFalse();
        run.Error.Should().NotBeNullOrEmpty();
        run.FileName.Should().BeEmpty();
        run.SizeBytes.Should().Be(0);
    }
}

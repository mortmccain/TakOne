using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TakOne.Infrastructure.Services;
using Xunit;

namespace TakOne.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for the <see cref="DatabaseBackupHostedService.GetStatus"/>
/// directory-health diagnostics — the <c>DirectoryExists</c> /
/// <c>DirectoryWritable</c> probes and the in-memory
/// <c>LastRunNote</c> field.
///
/// WHY THESE MATTER: the run-history ledger append and the retention
/// sweep swallow IO failures BY DESIGN (a run's outcome must never fail
/// because its history could not be saved). Without the probes, a backup
/// directory the app cannot write to is indistinguishable from
/// "success + 0 files + 0 bytes" — exactly the silent trap of the
/// stranded-backups incident (see DEPLOYMENT.md, "Backup volume
/// permissions": a root-owned takone_backups volume made every run
/// succeed via the SQL-default-directory fallback while the ledger
/// stayed empty, so the explanatory notes were never persisted).
///
/// COVERAGE APPROACH: same harness as
/// <see cref="DatabaseBackupHostedServiceHistoryTests"/> — the service
/// is armed against an unreachable server so no run executes; the
/// diagnostics are pure filesystem observations on the configured
/// directory. The read-only scenario needs a real POSIX chmod (skipped
/// on Windows where chmod is a no-op, and self-skipping under root,
/// where DAC checks are bypassed and the scenario cannot be arranged).
/// </summary>
public class DatabaseBackupStatusDiagnosticsTests : IDisposable
{
    private readonly string _tempDir;

    public DatabaseBackupStatusDiagnosticsTests()
    {
        // A fresh temp directory per test — the directory is resolved
        // from configuration, so each test gets a clean slate.
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "takone-backup-diagnostics-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            // Permission-narrowing tests must restore access before the
            // recursive delete, or cleanup itself fails with EACCES.
            RestoreAllPermissions(_tempDir);
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

    // ── Helpers (same shape as the sibling backup suites) ───────────

    /// <summary>
    /// Builds the service with an ARMED configuration (enabled + an
    /// unreachable connection string + the test's directory) and runs
    /// one Start/Stop cycle so ExecuteAsync resolves + caches the
    /// config. The next scheduled slot is hours away, so the loop only
    /// idles — no backup executes. See
    /// <see cref="DatabaseBackupHostedServiceHistoryTests"/> for the
    /// armed-poll rationale (.NET dispatches ExecuteAsync on the thread
    /// pool; the RetentionDays flip observes the config block ran).
    /// </summary>
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
                ["TakOne:Database:ConnectionString"] =
                    "Server=takone-diagnostics-tests-unreachable;Database=TakOne;TrustServerCertificate=True"
            })
            .Build();

        var service = new DatabaseBackupHostedService(
            config,
            NullLogger<DatabaseBackupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.GetStatus().RetentionDays != retentionDays
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await service.StopAsync(CancellationToken.None);

        return service;
    }

    private static void RestoreAllPermissions(string root)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SysChmod(root, "755");
        }
    }

    /// <summary>Shells out to chmod (POSIX only; never called on Windows).</summary>
    private static void SysChmod(string path, string mode)
    {
        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("chmod", $"{mode} \"{path}\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false
            });
        process?.WaitForExit(5_000);
    }

    // ── Directory health probes ─────────────────────────────────────

    [Fact]
    public async Task GetStatus_WritableDirectory_ReportsExistsAndWritableAndLeavesNoProbeFiles()
    {
        // Arrange — a healthy, writable backup directory (the state the
        // takone-backup-init compose service guarantees).
        var service = await CreateArmedServiceAsync(_tempDir);

        // Act
        var status = service.GetStatus();

        // Assert — the card renders its normal "no files yet" state.
        status.DirectoryExists.Should().BeTrue();
        status.DirectoryWritable.Should().BeTrue();

        // The write probe cleans up after itself — nothing that could
        // ever match the *.bak ledger pattern (or anything at all)
        // remains behind.
        Directory.EnumerateFileSystemEntries(_tempDir)
            .Should().BeEmpty("the write probe must create + delete its temp file");
    }

    [Fact]
    public async Task GetStatus_MissingDirectory_ReportsNotExistsAndNotWritable()
    {
        // Arrange — the configured directory was never created (the
        // volume-not-mounted misconfiguration).
        var missing = Path.Combine(_tempDir, "never-created");
        var service = await CreateArmedServiceAsync(missing);

        // Act
        var status = service.GetStatus();

        // Assert — the card renders its "directory not found" state,
        // distinct from the legitimate "no files yet" state; files and
        // history both stay empty without throwing.
        status.DirectoryExists.Should().BeFalse();
        status.DirectoryWritable.Should().BeFalse();
        status.Files.Should().BeEmpty();
        status.RunHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatus_ReadOnlyDirectory_ReportsExistsButNotWritable()
    {
        // Arrange — the directory exists but the app's user cannot
        // write into it (the wrong-ownership volume: mode 0555).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // chmod is a no-op on Windows — the scenario cannot be arranged.
        }

        var readOnlyDir = Path.Combine(_tempDir, "read-only-volume");
        Directory.CreateDirectory(readOnlyDir);
        SysChmod(readOnlyDir, "555");

        // Self-skip under root: DAC checks are bypassed there, so the
        // probe would succeed and the scenario cannot be arranged. A
        // canary write tells us which world we are in.
        try
        {
            var canary = Path.Combine(readOnlyDir, "canary");
            File.WriteAllText(canary, string.Empty);
            File.Delete(canary);
            SysChmod(readOnlyDir, "755");
            return; // privileged context — nothing to assert here.
        }
        catch (UnauthorizedAccessException)
        {
            // The expected non-root arrangement — fall through.
        }

        try
        {
            var service = await CreateArmedServiceAsync(readOnlyDir);

            // Act
            var status = service.GetStatus();

            // Assert — exists (the *.bak scan keeps working) but NOT
            // writable: the card's read-only warning state that makes
            // the silent ledger/sweep failures visible.
            status.DirectoryExists.Should().BeTrue();
            status.DirectoryWritable.Should().BeFalse();
        }
        finally
        {
            SysChmod(readOnlyDir, "755");
        }
    }

    // ── LastRunNote (in-memory twin of the ledger record's Note) ────

    [Fact]
    public async Task GetStatus_BeforeAnyRun_LastRunNoteIsEmpty()
    {
        // Arrange — armed, no run has ever completed.
        var service = await CreateArmedServiceAsync(_tempDir);

        // Act
        var status = service.GetStatus();

        // Assert — the success-with-notice alert stays hidden before
        // any run completes.
        status.LastRunNote.Should().BeEmpty();
    }
}

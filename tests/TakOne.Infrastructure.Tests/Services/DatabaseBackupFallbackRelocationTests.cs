using System.Runtime.InteropServices;
using FluentAssertions;
using TakOne.Infrastructure.Services;
using Xunit;

namespace TakOne.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for the RELOCATION half of the default-backup-directory
/// fallback (see <c>DatabaseBackupHostedService</c>'s class remarks):
/// when SQL Server cannot write to the configured backups folder (the
/// Windows dev box — the service account has no write access to the
/// app's <c>backups/</c> inside the developer's profile), the run backs
/// up to the instance's OWN default backup directory and then brings the
/// file HOME via <see cref="DatabaseBackupHostedService.RelocateBackupFile"/>.
///
/// COVERAGE APPROACH (real file I/O in a temp sandbox):
///   • Move permitted → the file lives ONLY in the backups folder.
///   • Source directory not writable (delete denied, read allowed — the
///     classic NTFS stance for non-elevated apps on the instance folder)
///     → copy succeeds, source cannot be deleted →
///     <see cref="DatabaseBackupHostedService.RelocationOutcome.CopiedHomeSourceRemains"/>.
///   • Source file unreadable → even the copy fails →
///     <see cref="DatabaseBackupHostedService.RelocationOutcome.ServerOnly"/>
///     (the run still succeeds; the note tells the admin where the file
///     remains).
/// The permission scenarios need a real POSIX chmod — the suite runs as
/// a non-root user in CI (skipped as informational on Windows, where
/// chmod is a no-op).
/// </summary>
public class DatabaseBackupFallbackRelocationTests : IDisposable
{
    private readonly string _tempDir;

    public DatabaseBackupFallbackRelocationTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "takone-backup-fallback-tests-" + Guid.NewGuid().ToString("N"));
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

    private static void RestoreAllPermissions(string root)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    SysChmod(dir, ChmodReadWriteExecute);
                }
            }
            catch (IOException)
            {
                // Best-effort — the top-level restore below usually suffices.
            }

            SysChmod(root, ChmodReadWriteExecute);
        }
    }

    private const string ChmodReadWriteExecute = "755";

    /// <summary>Shells out to chmod (POSIX only; a no-op is NOT called on Windows).</summary>
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

    private static bool CanRunPosixPermissionScenarios =>
        !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private (string ServerDir, string ServerFile, string LocalFile) ArrangeServerAndLocal()
    {
        var serverDir = Path.Combine(_tempDir, "sql-instance-backup");
        Directory.CreateDirectory(serverDir);
        var serverFile = Path.Combine(serverDir, "TakOne-backup-20260101-033000.bak");
        File.WriteAllText(serverFile, "backup payload");
        var localFile = Path.Combine(_tempDir, "backups", "TakOne-backup-20260101-033000.bak");
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        return (serverDir, serverFile, localFile);
    }

    [Fact]
    public void RelocateBackupFile_WhenMoveIsPermitted_FileLivesOnlyInBackupsFolder()
    {
        // Arrange — the happy path (e.g. an elevated dev session): the
        // app may both read and delete inside the SQL instance folder.
        var (_, serverFile, localFile) = ArrangeServerAndLocal();

        // Act
        var (outcome, detail) = DatabaseBackupHostedService.RelocateBackupFile(serverFile, localFile);

        // Assert — a real MOVE: no duplicate ever exists on disk.
        outcome.Should().Be(DatabaseBackupHostedService.RelocationOutcome.MovedHome);
        detail.Should().BeEmpty();
        File.Exists(serverFile).Should().BeFalse("the move removed the server-side original");
        File.Exists(localFile).Should().BeTrue("the file now lives in the backups folder");
        File.ReadAllText(localFile).Should().Be("backup payload", "the content survived the move");
    }

    [Fact]
    public void RelocateBackupFile_WhenSourceDirectoryIsNotWritable_CopiesHomeSourceRemains()
    {
        if (!CanRunPosixPermissionScenarios)
        {
            return; // chmod is a no-op on Windows — the scenario cannot be arranged
        }

        // Arrange — the classic non-elevated stance on the SQL instance
        // folder: READ allowed (the copy works), DELETE denied (no write
        // bit on the directory — the move/unlink fails).
        var (serverDir, serverFile, localFile) = ArrangeServerAndLocal();
        SysChmod(serverDir, "555"); // r-x — read+traverse, no write (no unlink)
        try
        {
            // Act
            var (outcome, detail) = DatabaseBackupHostedService.RelocateBackupFile(serverFile, localFile);

            // Assert — copied home; the server-side original could not be
            // deleted (the run note tells the admin to remove it manually).
            outcome.Should().Be(DatabaseBackupHostedService.RelocationOutcome.CopiedHomeSourceRemains);
            detail.Should().NotBeNullOrEmpty("the delete failure's reason is reported for the note");
            File.Exists(localFile).Should().BeTrue("the copy landed in the backups folder");
            File.ReadAllText(localFile).Should().Be("backup payload");
            File.Exists(serverFile).Should().BeTrue("the undeletable original remains on the server");
        }
        finally
        {
            SysChmod(serverDir, ChmodReadWriteExecute); // restore for cleanup
        }
    }

    [Fact]
    public void RelocateBackupFile_WhenSourceDirectoryIsInaccessible_ReportsServerOnly()
    {
        if (!CanRunPosixPermissionScenarios)
        {
            return; // chmod is a no-op on Windows — the scenario cannot be arranged
        }

        // Arrange — worst case: the SQL instance folder is completely
        // inaccessible to the app (neither move/rename nor read/copy can
        // run — the directory grants no permissions at all). The backup
        // still succeeded ON THE SERVER — the outcome that drives the
        // "file remains ON THE SQL SERVER at …" note.
        var (serverDir, serverFile, localFile) = ArrangeServerAndLocal();
        SysChmod(serverDir, "000"); // no read, no traverse, no write
        try
        {
            // Act
            var (outcome, detail) = DatabaseBackupHostedService.RelocateBackupFile(serverFile, localFile);

            // Assert — nothing the app can do; the file stays on the server.
            outcome.Should().Be(DatabaseBackupHostedService.RelocationOutcome.ServerOnly);
            detail.Should().NotBeNullOrEmpty("the copy failure's reason is reported for the note");
            File.Exists(localFile).Should().BeFalse("nothing landed in the backups folder");
        }
        finally
        {
            SysChmod(serverDir, ChmodReadWriteExecute); // restore for cleanup
            File.Exists(serverFile).Should().BeTrue("the backup file remains on the server");
        }
    }

    [Fact]
    public void RelocateBackupFile_WhenSourceIsMissing_ReportsServerOnly()
    {
        // Arrange — the file vanished between the BACKUP and the
        // relocation (e.g. an admin script swept the folder). Neither
        // move nor copy can run — the honest outcome is ServerOnly.
        var localFile = Path.Combine(_tempDir, "backups", "TakOne-backup-20260101-033000.bak");
        Directory.CreateDirectory(Path.GetDirectoryName(localFile)!);
        var missingServerFile = Path.Combine(_tempDir, "sql-instance-backup", "TakOne-backup-20260101-033000.bak");

        // Act
        var (outcome, detail) = DatabaseBackupHostedService.RelocateBackupFile(missingServerFile, localFile);

        // Assert
        outcome.Should().Be(DatabaseBackupHostedService.RelocationOutcome.ServerOnly);
        detail.Should().NotBeNullOrEmpty();
        File.Exists(localFile).Should().BeFalse();
    }
}

using FluentAssertions;
using TakOne.Infrastructure.Services;
using Xunit;

namespace TakOne.Infrastructure.Tests.Services;

/// <summary>
/// Unit tests for the server-side backup PATH RESOLUTION + the BACKUP
/// command's path allowlist — the regression suite for the "relative
/// default path" defect reported from a Windows test deployment:
///
/// The shipped default was a RELATIVE <c>backups</c> value, and SQL
/// Server resolves a relative <c>TO DISK</c> path against ITS OWN
/// instance backup folder (<c>C:\Program Files\Microsoft SQL
/// Server\MSSQL17.&lt;INSTANCE&gt;\MSSQL\Backup\</c>) — never the app's
/// working directory — so every single-machine run failed with
/// <c>Cannot open backup device … Operating system error 3</c>.
///
/// The fix under test:
///   • <see cref="DatabaseBackupHostedService.ResolveServerPath"/> —
///     relative values are anchored at the app's working directory
///     (the same base the web-side directory uses), so the app and SQL
///     Server address ONE physical folder on a single-machine box;
///     absolute values (native-rooted OR Windows drive-letter) pass
///     through verbatim.
///   • <see cref="DatabaseBackupHostedService.IsValidBackupPath"/> —
///     the colon is now allowed ONLY at the Windows drive-letter
///     position, so absolute Windows paths reach the command while the
///     N'…' literal breakout boundary (the single quote) stays closed.
/// </summary>
public class DatabaseBackupServerPathTests
{
    // ── ResolveServerPath ──────────────────────────────────────────

    [Fact]
    public void ResolveServerPath_NullConfig_FallsBackToDirectoryMadeAbsolute()
    {
        // The repo default: no ServerPath configured, relative Directory.
        // Must come back ABSOLUTE (anchored at the app's working
        // directory) — a relative value handed to BACKUP … TO DISK is
        // resolved by SQL Server against ITS OWN backup folder and
        // fails with "Operating system error 3".
        var resolved = DatabaseBackupHostedService.ResolveServerPath(null, "backups");

        resolved.Should().NotBeNullOrWhiteSpace();
        Path.IsPathRooted(resolved).Should().BeTrue(
            "a relative TO DISK path is resolved by SQL Server against its own instance backup folder, " +
            "so the app must hand SQL Server an absolute path");
        resolved.Should().EndWith("backups", "the configured directory name is preserved, only anchored");
    }

    [Fact]
    public void ResolveServerPath_WhitespaceConfig_FallsBackToDirectoryMadeAbsolute()
    {
        var resolved = DatabaseBackupHostedService.ResolveServerPath("   ", "backups");

        Path.IsPathRooted(resolved).Should().BeTrue();
    }

    [Fact]
    public void ResolveServerPath_RelativeExplicitValue_IsAnchoredAtWorkingDirectory()
    {
        // Even an EXPLICIT relative ServerPath (e.g. an appsettings that
        // still says "backups") must be anchored — a relative value is
        // never meaningful for a remote server and is broken for a
        // same-machine server, so anchoring is always the fix.
        var resolved = DatabaseBackupHostedService.ResolveServerPath("backups", "/var/lib/takone/elsewhere");

        Path.IsPathRooted(resolved).Should().BeTrue();
        resolved.Should().EndWith("backups");
    }

    [Fact]
    public void ResolveServerPath_NativeAbsolutePath_IsPassedThroughVerbatim()
    {
        // Docker's configured DB-side mount — must never be re-anchored.
        const string absolute = "/var/opt/mssql/backup";

        var resolved = DatabaseBackupHostedService.ResolveServerPath(absolute, "/var/lib/takone/backups");

        resolved.Should().Be(absolute);
    }

    [Fact]
    public void ResolveServerPath_WindowsDrivePath_IsPassedThroughVerbatimOnAnyOs()
    {
        // A Linux app may point at a remote Windows SQL Server — the
        // drive-letter path is absolute on the SERVER's filesystem and
        // must survive the Linux-side resolution untouched (Path.
        // IsPathRooted would say false on Linux for "C:\…").
        const string windows = @"C:\ProgramData\TakOne\backups";

        var resolved = DatabaseBackupHostedService.ResolveServerPath(windows, "/var/lib/takone/backups");

        resolved.Should().Be(windows);
    }

    // ── BuildBackupCommandText / IsValidBackupPath ─────────────────

    [Fact]
    public void BuildBackupCommandText_AbsoluteWindowsPath_IncludesVerbatimDevice()
    {
        // The user-reported failure scenario: an absolute Windows path
        // (with drive-letter colon) must be accepted now that
        // ResolveServerPath anchors relative defaults to absolute.
        const string device = @"C:\TakOne\backups\TakOne-backup-20260101-033000.bak";

        var sql = DatabaseBackupHostedService.BuildBackupCommandText("TakOne", device);

        sql.Should().Contain($"TO DISK = N'{device}'");
        sql.Should().Contain("BACKUP DATABASE [TakOne]");
        sql.Should().Contain("WITH INIT, CHECKSUM");
    }

    [Fact]
    public void BuildBackupCommandText_AbsoluteUnixPath_IncludesVerbatimDevice()
    {
        // The Docker path — unchanged behavior.
        const string device = "/var/opt/mssql/backup/TakOne-backup-20260101-033000.bak";

        var sql = DatabaseBackupHostedService.BuildBackupCommandText("TakOne", device);

        sql.Should().Contain($"TO DISK = N'{device}'");
    }

    [Fact]
    public void BuildBackupCommandText_SingleQuoteInPath_IsRejected()
    {
        // The single quote is the ONLY character that could break out of
        // the N'…' literal — the allowlist must keep rejecting it even
        // though drive-letter colons are now admitted.
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseBackupHostedService.BuildBackupCommandText("TakOne", @"C:\backups\o'brien.bak"));
    }

    [Fact]
    public void BuildBackupCommandText_ColonOutsideDrivePosition_IsRejected()
    {
        // A colon is legal ONLY as the drive-letter separator — anywhere
        // else it stays outside the allowlist.
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseBackupHostedService.BuildBackupCommandText("TakOne", @"C:\backups\foo:bar.bak"));
    }

    [Fact]
    public void BuildBackupCommandText_NonLetterDriveCharacter_IsRejected()
    {
        // "1:\backups" — colon at position 1 but no ASCII letter before
        // it: not a drive letter, rejected.
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseBackupHostedService.BuildBackupCommandText("TakOne", @"1:\backups\x.bak"));
    }

    [Fact]
    public void BuildBackupCommandText_EmptyPath_IsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseBackupHostedService.BuildBackupCommandText("TakOne", "   "));
    }

    // ── Default-backup-directory fallback paths (spaces) ─────────────
    // The fallback reads the instance's own default backup directory
    // from the registry — on virtually every Windows install that is
    // "C:\Program Files\Microsoft SQL Server\MSSQLnn.INSTANCE\MSSQL\Backup"
    // — and that registry-sourced path must pass the SAME allowlist as
    // configuration-sourced paths before it reaches command text. Spaces
    // are the one additional character the real default directory needs.

    [Fact]
    public void IsValidBackupPath_InstanceDefaultDirectoryWithSpaces_IsValid()
    {
        // The canonical Windows instance default backup directory +
        // file, exactly what xp_instance_regread returns on a dev box.
        const string instanceDefault =
            @"C:\Program Files\Microsoft SQL Server\MSSQL17.HANZ\MSSQL\Backup\TakOne-backup-20260101-033000.bak";

        DatabaseBackupHostedService.IsValidBackupPath(instanceDefault).Should().BeTrue(
            "SQL Server's own default backup directory lives under 'C:\\Program Files\\…' — " +
            "the fallback's registry-sourced path must pass the allowlist");
    }

    [Fact]
    public void BuildBackupCommandText_InstanceDefaultDirectoryPath_IsEmbeddedVerbatim()
    {
        const string device =
            @"C:\Program Files\Microsoft SQL Server\MSSQL17.HANZ\MSSQL\Backup/TakOne-backup-20260101-033000.bak";

        var sql = DatabaseBackupHostedService.BuildBackupCommandText("TakOne", device);

        sql.Should().Contain($"TO DISK = N'{device}'");
    }

    [Fact]
    public void BuildBackupCommandText_SpaceWithQuote_IsStillRejected()
    {
        // Widening the allowlist with a space must not re-open the N'…'
        // literal breakout boundary — the quote stays banned.
        Assert.Throws<InvalidOperationException>(() =>
            DatabaseBackupHostedService.BuildBackupCommandText(
                "TakOne",
                @"C:\Program Files\o'brien\TakOne-backup.bak"));
    }
}

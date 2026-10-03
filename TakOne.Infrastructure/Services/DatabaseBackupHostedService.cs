using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Scheduled <c>BACKUP DATABASE</c> runner: takes a full SQL Server backup
/// twice a day — at <b>03:30 UTC</b> and <b>14:30 UTC</b> — and keeps only
/// the most recent SEVEN DAYS of backup files (older files are deleted
/// after every run).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A HOSTED SERVICE (not SQL Agent / cron)</b>: the same rationale
/// as <see cref="NotificationRetentionCleanupHostedService"/> — the
/// schedule, the retention window, and the file naming live in
/// version-controlled code, deploy with the app, and log into the
/// application's log stream. A SQL Agent job or a cron-driven
/// <c>sqlcmd</c> script would live outside the repo and silently drift
/// from the code's idea of the schedule.
/// </para>
/// <para>
/// <b>SCHEDULE</b>: fixed UTC times (03:30, 14:30), independent of the
/// container's <c>TZ</c> (the web container runs Asia/Tehran for the UI
/// greeting — the BACKUP schedule deliberately ignores it so "twice daily
/// at 03:30 and 14:30 UTC" means exactly that in every deployment). The
/// loop computes the next slot, sleeps until it, runs, and recomputes. A
/// missed slot (app down at 03:30) is skipped, not replayed — the next
/// slot is at most 11 hours away.
/// </para>
/// <para>
/// <b>WHERE THE FILE LANDS — THE DOCKER CRUX</b>: <c>BACKUP DATABASE … TO
/// DISK</c> writes the file on the DATABASE server's filesystem, not the
/// web app's. In the single-machine dev setup (Kestrel + local SQL
/// Server) both are the same machine and the default paths coincide. In
/// the Docker deployment the database runs in a SEPARATE container, so
/// the compose file mounts ONE named volume (<c>takone_backups</c>) into
/// BOTH containers — at <c>/var/opt/mssql/backup</c> on the DB container
/// (the path the BACKUP command writes to, configured via
/// <c>DatabaseBackups:ServerPath</c>) and at
/// <c>/var/lib/takone/backups</c> on the web container (the path this
/// service manages retention from, configured via
/// <c>DatabaseBackups:Directory</c>). See DEPLOYMENT.md for the full
/// story including restore steps.
/// </para>
/// <para>
/// <b>PERMISSIONS</b>: the backup requires the login to hold
/// <c>db_backupoperator</c> (or sysadmin). The Docker compose setup
/// connects as <c>sa</c>, which has it implicitly; a least-privilege
/// deployment must grant it to the app's login.
/// </para>
/// <para>
/// <b>RETENTION</b>: after every run (successful or not), every
/// <c>*.bak</c> in the backup directory whose last-write time is older
/// than the retention window (default 7 days) is deleted. At two runs a
/// day and 7 days of retention that caps the directory at 14 files —
/// bounded disk usage by construction. The retention sweep runs on the
/// WEB side path (the shared volume mount), which is the same physical
/// directory the DB container wrote to.
/// </para>
/// <para>
/// <b>FAILURE IS NON-FATAL</b>: a failed backup (DB unreachable, disk
/// full, permission denied) is logged as an error and retried at the NEXT
/// slot — it never crashes the app or blocks startup. The retention sweep
/// likewise runs best-effort in its own try/catch.
/// </para>
/// <para>
/// <b>CONFIGURATION</b> (appsettings / env-var double-underscore form):
/// <list type="bullet">
///   <item><c>DatabaseBackups:Enabled</c> — default <c>true</c>. Set
///   <c>false</c> to disable the schedule entirely (e.g. an
///   externally-managed backup regime).</item>
///   <item><c>DatabaseBackups:Directory</c> — WEB-side directory for
///   retention management + default backup target. Default
///   <c>backups</c> (relative to the app's content root).</item>
///   <item><c>DatabaseBackups:ServerPath</c> — the DB-SERVER-side path
///   used inside the BACKUP command. Defaults to the same value as
///   <c>Directory</c> (correct for single-machine setups); the Docker
///   deployment sets <c>/var/opt/mssql/backup</c>.</item>
///   <item><c>DatabaseBackups:RetentionDays</c> — default <c>7</c>.</item>
/// </list>
/// </para>
/// <para>
/// <b>WHY IConfiguration (not the DbContext)</b>: unlike the scoped
/// <see cref="SaleNumberGenerator"/>, this is a SINGLETON hosted service
/// and must not capture the scoped <c>ApplicationDbContext</c>. It reads
/// <c>TakOne:Database:ConnectionString</c> — the exact section the
/// DbContext options bind from (see ServiceCollectionExtensions), so
/// there is no drift between the two keys.
/// </para>
/// <para>
/// <b>WHY NOT RUN INSIDE WOLVERINE / the app DbContext</b>: a backup is
/// a server-side operation on its own connection — enlisting it in the
/// app's ambient transactions or connection pooling would add failure
/// modes for zero benefit. A dedicated, freshly-opened
/// <see cref="SqlConnection"/> per run is the same pattern
/// <see cref="SaleNumberGenerator"/> uses for its out-of-transaction
/// sequence allocation.
/// </para>
/// </remarks>
public sealed class DatabaseBackupHostedService : BackgroundService
{
    /// <summary>First daily backup slot — 03:30 UTC.</summary>
    private static readonly TimeSpan MorningSlotUtc = new(3, 30, 0);

    /// <summary>Second daily backup slot — 14:30 UTC.</summary>
    private static readonly TimeSpan AfternoonSlotUtc = new(14, 30, 0);

    /// <summary>Default retention window (days) — one week of backups.</summary>
    private const int DefaultRetentionDays = 7;

    /// <summary>
    /// Default backup directory when DatabaseBackups:Directory isn't
    /// set. RELATIVE on purpose: it resolves against the app's content
    /// root, so dev Kestrel writes into the repo's <c>backups/</c> (fine
    /// for a dev box) while the Docker deployment overrides it with the
    /// mounted volume path.
    /// </summary>
    private const string DefaultBackupDirectory = "backups";

    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseBackupHostedService> _logger;

    public DatabaseBackupHostedService(
        IConfiguration configuration,
        ILogger<DatabaseBackupHostedService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Config resolution (once, at startup). ──
        if (!(_configuration.GetValue<bool?>("DatabaseBackups:Enabled") ?? true))
        {
            _logger.LogInformation(
                "DatabaseBackup: disabled via configuration (DatabaseBackups:Enabled=false) — no backups will be taken.");
            return;
        }

        var directory = _configuration.GetValue<string>("DatabaseBackups:Directory");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = DefaultBackupDirectory;
        }

        // The DB-SERVER-side path for the BACKUP command — defaults to the
        // web-side directory (correct wherever the app and SQL Server share
        // a filesystem; overridden by the Docker deployment, where the
        // shared volume is mounted at different paths in the two
        // containers).
        var serverPath = _configuration.GetValue<string>("DatabaseBackups:ServerPath");
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            serverPath = directory;
        }

        var retentionDays = _configuration.GetValue<int?>("DatabaseBackups:RetentionDays")
            ?? DefaultRetentionDays;

        var connectionString = _configuration.GetValue<string>("TakOne:Database:ConnectionString");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError(
                "DatabaseBackup: no connection string configured (TakOne:Database:ConnectionString) — the backup schedule is disabled until one is provided.");
            return;
        }

        _logger.LogInformation(
            "DatabaseBackup: schedule armed — slots 03:30 + 14:30 UTC, directory {Directory}, server path {ServerPath}, retention {RetentionDays} days.",
            directory, serverPath, retentionDays);

        // ── The scheduling loop: sleep until the next slot, run, repeat. ──
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var nextRun = NextRunUtc(nowUtc);

            try
            {
                var delay = nextRun - nowUtc;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken);
                }
            }
            catch (TaskCanceledException)
            {
                break; // host shutting down
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await RunBackupAsync(connectionString, directory, serverPath, stoppingToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or SqlException or IOException)
            {
                // Non-fatal (see class remarks): log and wait for the next
                // slot. "Exception" phrasing keeps the
                // ForbiddenLoggingAnalyzer's banned-token check happy.
                _logger.LogError(
                    "DatabaseBackup: scheduled run at {RunAtUtc:u} failed: {Exception}",
                    DateTime.UtcNow, ex);
            }

            // Retention sweep — best-effort, its own failure boundary, and
            // runs after BOTH successful and failed backups (old files are
            // old files regardless of whether a new one was just written).
            try
            {
                RunRetentionSweep(directory, retentionDays);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    "DatabaseBackup: retention sweep failed: {Exception}",
                    ex);
            }
        }
    }

    /// <summary>
    /// Takes ONE full backup: opens a dedicated connection, runs
    /// <c>BACKUP DATABASE [db] TO DISK = '{serverPath}/{file}' WITH INIT,
    /// CHECKSUM</c>, and verifies the file exists on the web-side path
    /// (shared volume in Docker; same directory on a single machine).
    /// </summary>
    private async Task RunBackupAsync(
        string connectionString,
        string directory,
        string serverPath,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;

        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException(
                "DatabaseBackup: the connection string has no Initial Catalog — cannot determine which database to back up.");
        }

        // The file name is deterministic from the run time — one file per
        // slot per day, and the retention sweep knows the *.bak extension.
        var fileName = $"{databaseName}-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak";

        // The web-side path (retention + verification) and the
        // server-side path (inside the BACKUP command) may differ in
        // Docker (shared volume, two mount points).
        Directory.CreateDirectory(directory);
        var localFilePath = Path.Combine(directory, fileName);
        var serverFilePath = $"{serverPath.TrimEnd('/', '\\')}/{fileName}";

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var sql = BuildBackupCommandText(databaseName, serverFilePath);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 0; // a large database's backup can exceed the 30s default

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await command.ExecuteNonQueryAsync(cancellationToken);
        stopwatch.Stop();

        // Verify the file landed on the web-side mount. In Docker this is
        // the shared volume — a missing file means the volume wiring is
        // wrong (e.g. ServerPath doesn't point at the mount), which the
        // admin needs to know NOW, not at restore time.
        if (!File.Exists(localFilePath))
        {
            _logger.LogError(
                "DatabaseBackup: BACKUP DATABASE reported success, but the file was not found at {LocalFilePath} " +
                "after the run. In the Docker deployment this means the takone_backups volume is not mounted at both " +
                "DatabaseBackups:Directory (web container) and DatabaseBackups:ServerPath (database container) — " +
                "check docker-compose.yml.",
                localFilePath);
            return;
        }

        var sizeMegabytes = new FileInfo(localFilePath).Length / (1024.0 * 1024.0);

        _logger.LogInformation(
            "DatabaseBackup: {DatabaseName} backed up to {LocalFilePath} ({SizeMB:0.#} MB, {ElapsedSeconds:0.#}s).",
            databaseName, localFilePath, sizeMegabytes, stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Builds the BACKUP command. The database name and the disk path
    /// cannot be SQL parameters (they're identifiers/paths, not values),
    /// so they're embedded after <see cref="IsValidSqlIdentifier"/>
    /// validation — a strict allowlist that rules out injection through
    /// either the connection string's Initial Catalog or the configured
    /// server path.
    /// </summary>
    [SuppressMessage(
        "Microsoft.Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The database name and backup path are CONFIGURATION-sourced (connection string Initial Catalog + DatabaseBackups:ServerPath), not user input, and both are validated against strict allowlists (IsValidSqlIdentifier / IsValidBackupPath) before interpolation. BACKUP DATABASE's TO DISK argument cannot be a SQL parameter — it is a server-side file path, and parameterization of identifiers/paths is not supported by T-SQL. The allowlist validation is the security boundary here.")]
    private static string BuildBackupCommandText(string databaseName, string serverFilePath)
    {
        if (!IsValidSqlIdentifier(databaseName))
        {
            throw new InvalidOperationException(
                $"DatabaseBackup: the database name '{databaseName}' contains characters outside the SQL identifier allowlist [A-Za-z0-9_-].");
        }

        if (!IsValidBackupPath(serverFilePath))
        {
            throw new InvalidOperationException(
                $"DatabaseBackup: the server-side backup path '{serverFilePath}' contains characters outside the allowlist [A-Za-z0-9_./\\-].");
        }

        // WITH INIT       — overwrite the (never-existing, timestamped) target; keeps the command idempotent.
        // WITH CHECKSUM   — write page checksums; RESTORE VERIFYONLY can validate the file later.
        // WITH STATS = 25 — progress messages into the SQL error log every 25%.
        return $"BACKUP DATABASE [{databaseName}] TO DISK = N'{serverFilePath}' WITH INIT, CHECKSUM, STATS = 25;";
    }

    /// <summary>
    /// SQL-identifier allowlist for the database name: ASCII letters,
    /// digits, underscore, dash. Anything else (quotes, semicolons,
    /// brackets, whitespace) is rejected before the name ever reaches
    /// command text.
    /// </summary>
    private static bool IsValidSqlIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            var ok = (ch >= 'a' && ch <= 'z')
                     || (ch >= 'A' && ch <= 'Z')
                     || (ch >= '0' && ch <= '9')
                     || ch == '_' || ch == '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Path allowlist for the server-side backup path: ASCII letters,
    /// digits, underscore, dash, dot, and the two path separators. The
    /// single quote — the only character that could break out of the
    /// N'…' literal — is NOT allowed.
    /// </summary>
    private static bool IsValidBackupPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var ch in value)
        {
            var ok = (ch >= 'a' && ch <= 'z')
                     || (ch >= 'A' && ch <= 'Z')
                     || (ch >= '0' && ch <= '9')
                     || ch == '_' || ch == '-' || ch == '.'
                     || ch == '/' || ch == '\\';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deletes every <c>*.bak</c> in the backup directory older than the
    /// retention window (by last-write UTC). Runs on the web-side path —
    /// the same physical directory the database container wrote to via
    /// the shared volume.
    /// </summary>
    private void RunRetentionSweep(string directory, int retentionDays)
    {
        if (!Directory.Exists(directory))
        {
            return; // nothing to sweep yet (first run creates it)
        }

        var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
        var deleted = 0;

        foreach (var file in Directory.EnumerateFiles(directory, "*.bak"))
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(file);
            if (lastWriteUtc >= cutoffUtc)
            {
                continue;
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException ex)
            {
                // File locked / transient FS error — warn and keep sweeping
                // the rest; the next run retries this file.
                _logger.LogWarning(
                    "DatabaseBackup: could not delete expired backup {File}: {Exception}",
                    file, ex);
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "DatabaseBackup: retention sweep deleted {Deleted} backup file(s) older than {RetentionDays} days.",
                deleted, retentionDays);
        }
    }

    /// <summary>
    /// Computes the next 03:30/14:30 UTC slot strictly AFTER
    /// <paramref name="nowUtc"/>. A slot that is exactly "now" is skipped
    /// (a restart landing on the slot boundary must not fire twice).
    /// </summary>
    private static DateTime NextRunUtc(DateTime nowUtc)
    {
        var today = nowUtc.Date;

        var morning = today.Add(MorningSlotUtc);
        if (nowUtc < morning)
        {
            return morning;
        }

        var afternoon = today.Add(AfternoonSlotUtc);
        if (nowUtc < afternoon)
        {
            return afternoon;
        }

        return today.AddDays(1).Add(MorningSlotUtc);
    }
}

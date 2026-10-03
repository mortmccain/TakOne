using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Common.Models;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Scheduled <c>BACKUP DATABASE</c> runner: takes a full SQL Server backup
/// twice a day — at <b>03:30 UTC</b> and <b>14:30 UTC</b> — and keeps only
/// the most recent SEVEN DAYS of backup files (older files are deleted
/// after every run).
/// </summary>
/// <remarks>
/// <para>
/// <b>DUAL ROLE — HOSTED SERVICE + IBackupCoordinator</b>: this singleton
/// is registered BOTH as the IHostedService (the schedule loop) and as the
/// Application-layer <see cref="IBackupCoordinator"/> port (status
/// snapshot + manual trigger for the Settings page's admin backup card).
/// The run-state fields below are guarded by <see cref="_stateLock"/>
/// because the coordinator is invoked from Wolverine handler threads while
/// the loop mutates the same state.
/// </para>
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
///   deployment sets <c>/var/opt/mssql/backup</c>. RELATIVE values are
///   resolved to ABSOLUTE against the app's working directory before
///   the command runs (see <see cref="ResolveServerPath"/>) — SQL
///   Server resolves a relative <c>TO DISK</c> path against ITS OWN
///   instance backup folder (e.g. <c>C:\Program Files\Microsoft SQL
///   Server\MSSQL17.&lt;INSTANCE&gt;\MSSQL\Backup\</c>), which is never
///   where the app created its <c>backups/</c> folder, so a raw
///   relative path makes every single-machine run fail with
///   "Operating system error 3". Windows drive-letter paths
///   (<c>C:\…</c>) and native absolute paths pass through verbatim,
///   so a Linux app pointing at a remote Windows SQL Server still
///   works.</item>
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
/// <para>
/// <b>RUN-HISTORY LEDGER</b>: every COMPLETED run (success or failure,
/// scheduled or manual) is appended as one JSON line to
/// <c>{backup-directory}/backup-history.jsonl</c> — surviving restarts,
/// unlike the in-memory last-run snapshot. Records whose finish time
/// falls outside the retention window are pruned by the same sweep that
/// deletes the old <c>.bak</c> files (plus a defensive 500-record disk
/// cap); <see cref="GetStatus"/> returns the newest 50 as
/// <see cref="BackupStatusDto.RunHistory"/>. The ledger is best-effort:
/// a failed history write is logged as a warning and NEVER fails an
/// otherwise-successful backup.
/// </para>
/// <para>
/// <b>MANUAL TRIGGER SEMANTICS</b>: <see cref="TryTriggerNow"/> completes
/// a <see cref="TaskCompletionSource"/> the schedule loop races its
/// slot-delay against — the loop wakes, runs the backup on ITS OWN
/// background execution (the caller never awaits the backup itself), then
/// recomputes the next fixed slot. A manual run therefore never shifts the
/// schedule, and at most ONE run executes at a time by construction (the
/// loop is the only runner).
/// </para>
/// </remarks>
public sealed class DatabaseBackupHostedService : BackgroundService, IBackupCoordinator
{
    /// <summary>First daily backup slot — 03:30 UTC.</summary>
    private static readonly TimeSpan MorningSlotUtc = new(3, 30, 0);

    /// <summary>Second daily backup slot — 14:30 UTC.</summary>
    private static readonly TimeSpan AfternoonSlotUtc = new(14, 30, 0);

    /// <summary>Default retention window (days) — one week of backups.</summary>
    private const int DefaultRetentionDays = 7;

    /// <summary>
    /// The run-history ledger file (JSONL, one record per completed run)
    /// written NEXT TO the <c>.bak</c> files in the backup directory.
    /// </summary>
    private const string HistoryFileName = "backup-history.jsonl";

    /// <summary>
    /// How many run records <see cref="GetStatus"/> returns (newest first)
    /// — plenty for the Settings card's ledger while keeping the status
    /// payload tight.
    /// </summary>
    private const int MaxHistoryRecordsReturned = 50;

    /// <summary>
    /// Defensive hard cap on records kept on disk during the prune — the
    /// retention window bounds scheduled records (2/day × retention) but
    /// a manual-trigger spree shouldn't grow the file unboundedly.
    /// </summary>
    private const int MaxHistoryRecordsOnDisk = 500;

    /// <summary>
    /// Shared JSON options for the ledger: case-insensitive so a record
    /// written by a future serialization-naming change still parses
    /// (unknown members are skipped by default).
    /// </summary>
    private static readonly JsonSerializerOptions HistoryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

    // ── Coordinator state (guarded by _stateLock — see class remarks) ──

    /// <summary>Guards every coordinator-state read/write below.</summary>
    private readonly object _stateLock = new();

    /// <summary>True once ExecuteAsync resolved a usable configuration (enabled + connection string) — the single source of truth GetStatus reports as Enabled.</summary>
    private bool _armed;

    /// <summary>Cache of the web-side backup directory (retention + verification + status listing).</summary>
    private string _directory = DefaultBackupDirectory;

    /// <summary>Cache of the retention window (days).</summary>
    private int _retentionDays = DefaultRetentionDays;

    /// <summary>True while a scheduled or manual run is executing.</summary>
    private bool _isRunning;

    /// <summary>UTC start of the most recent run (null = never ran).</summary>
    private DateTime? _lastRunStartedUtc;

    /// <summary>UTC end of the most recent run (null = never finished).</summary>
    private DateTime? _lastRunFinishedUtc;

    /// <summary>Outcome of the most recent run (null = none completed yet).</summary>
    private bool? _lastRunSucceeded;

    /// <summary>Failure detail for the most recent run (empty unless it failed).</summary>
    private string _lastRunError = string.Empty;

    /// <summary>
    /// The manual-trigger wake signal + its pending flag. <see cref="TryTriggerNow"/>
    /// sets <c>_pendingTriggers</c> and completes the current source; the
    /// schedule loop races its slot-delay against the source and CONSUMES
    /// it at wake time (clearing the flag + swapping in a fresh source)
    /// before running. The consume-at-wake (not consume-at-trigger)
    /// placement is what makes a trigger that arrives while the loop is
    /// BUSY — mid-run, mid-sweep — survive: the flag stays set, the fresh
    /// source the loop reads next is already completed, and the next wait
    /// wakes immediately. <c>RunContinuationsAsynchronously</c> so a
    /// trigger from a Wolverine handler thread never runs the loop's
    /// continuation inline on the caller.
    /// </summary>
    private volatile TaskCompletionSource _triggerSignal = CreateTriggerSource();

    /// <summary>1 while a manual trigger has been accepted but not yet run (guarded by <see cref="_stateLock"/>).</summary>
    private int _pendingTriggers;

    private static TaskCompletionSource CreateTriggerSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DatabaseBackupHostedService(
        IConfiguration configuration,
        ILogger<DatabaseBackupHostedService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // ── Config resolution (once, at startup). The resolved values are
        //    cached into the coordinator state so GetStatus can report them
        //    without re-reading configuration on every request. ──
        var enabled = _configuration.GetValue<bool?>("DatabaseBackups:Enabled") ?? true;

        var directory = _configuration.GetValue<string>("DatabaseBackups:Directory");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = DefaultBackupDirectory;
        }

        var retentionDays = _configuration.GetValue<int?>("DatabaseBackups:RetentionDays")
            ?? DefaultRetentionDays;

        var connectionString = _configuration.GetValue<string>("TakOne:Database:ConnectionString");

        lock (_stateLock)
        {
            _directory = directory;
            _retentionDays = retentionDays;
            // Armed only when the schedule will actually run — a disabled
            // or unconnectable configuration still serves GetStatus with
            // Enabled=false instead of throwing.
            _armed = enabled && !string.IsNullOrWhiteSpace(connectionString);
        }

        if (!enabled)
        {
            _logger.LogInformation(
                "DatabaseBackup: disabled via configuration (DatabaseBackups:Enabled=false) — no backups will be taken.");
            return;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError(
                "DatabaseBackup: no connection string configured (TakOne:Database:ConnectionString) — the backup schedule is disabled until one is provided.");
            return;
        }

        // The DB-SERVER-side path for the BACKUP command — defaults to the
        // web-side directory (correct wherever the app and SQL Server share
        // a filesystem; overridden by the Docker deployment, where the
        // shared volume is mounted at different paths in the two
        // containers). RELATIVE values are resolved to ABSOLUTE first —
        // see ResolveServerPath: SQL Server resolves a relative TO DISK
        // path against its OWN instance backup folder, never the app's
        // working directory, which made the old relative default
        // ("backups") fail every single-machine run with "Operating
        // system error 3 (The system cannot find the path specified)".
        var configuredServerPath = _configuration.GetValue<string>("DatabaseBackups:ServerPath");
        var serverPath = ResolveServerPath(configuredServerPath, directory);

        _logger.LogInformation(
            "DatabaseBackup: schedule armed — slots 03:30 + 14:30 UTC, directory {Directory}, server path {ServerPath}, retention {RetentionDays} days.",
            directory, serverPath, retentionDays);

        // ── The scheduling loop: sleep until the next slot (OR until a
        //    manual trigger fires), run, repeat. ──
        while (!stoppingToken.IsCancellationRequested)
        {
            var nowUtc = DateTime.UtcNow;
            var nextRun = NextRunUtc(nowUtc);

            // Whether the run about to happen satisfies a manual trigger
            // (vs. a pure scheduled slot) — captured at wake time and
            // persisted into the run-history ledger's WasManual field.
            bool runWasManual;

            try
            {
                var delay = nextRun - nowUtc;
                if (delay > TimeSpan.Zero)
                {
                    // Race the slot-delay against the manual-trigger signal:
                    // whichever completes first wakes the loop.
                    using var wakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var delayTask = Task.Delay(delay, wakeCts.Token);
                    var triggerTask = _triggerSignal.Task;

                    var winner = await Task.WhenAny(delayTask, triggerTask);

                    // If the trigger won, cancel the pending delay so its
                    // timer doesn't linger (best-effort — the loop is about
                    // to run anyway).
                    if (winner != delayTask)
                    {
                        wakeCts.Cancel();
                    }
                }

                // ── Consume the trigger at WAKE time: clear the pending
                //    flag and swap in a fresh source BEFORE running, so a
                //    trigger that arrives DURING the run lands on the new
                //    source and wakes the NEXT wait (see the field's
                //    remarks). A trigger that fired just as the slot delay
                //    elapsed is satisfied by the run about to happen. ──
                lock (_stateLock)
                {
                    // The run about to execute is MANUAL iff a pending
                    // trigger is being consumed by this wake: the signal
                    // won the WhenAny race, or a trigger that arrived
                    // mid-run survived on the already-completed fresh
                    // source. A pure slot wake finds the flag at 0.
                    runWasManual = _pendingTriggers > 0;
                    _pendingTriggers = 0;
                    _triggerSignal = CreateTriggerSource();
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
                await RunBackupAsync(connectionString, directory, serverPath, runWasManual, stoppingToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or SqlException or IOException)
            {
                // Non-fatal (see class remarks): log and wait for the next
                // slot. "Exception" phrasing keeps the
                // ForbiddenLoggingAnalyzer's banned-token check happy.
                // (Covers scheduled AND manually-triggered runs — both
                // execute here on the loop.)
                _logger.LogError(
                    "DatabaseBackup: backup run failed: {Exception}",
                    ex);
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
    /// Records the run's start/end/outcome into the coordinator state
    /// (see <see cref="GetStatus"/>) AND appends a record to the
    /// JSONL run-history ledger (best-effort — see the class remarks).
    /// </summary>
    private async Task RunBackupAsync(
        string connectionString,
        string directory,
        string serverPath,
        bool wasManual,
        CancellationToken cancellationToken)
    {
        DateTime startedUtc;
        lock (_stateLock)
        {
            _isRunning = true;
            startedUtc = DateTime.UtcNow;
            _lastRunStartedUtc = startedUtc;
            _lastRunFinishedUtc = null;
            _lastRunSucceeded = null;
            _lastRunError = string.Empty;
        }

        // Locals for the history record — set on BOTH outcomes so the
        // finally-block append sees the truth regardless of success.
        var succeeded = false;
        var error = string.Empty;
        var backupFileName = string.Empty;
        var backupFileSizeBytes = 0L;

        try
        {
            var (localFilePath, sizeBytes) = await RunBackupCoreAsync(connectionString, directory, serverPath, cancellationToken);
            backupFileName = Path.GetFileName(localFilePath);
            backupFileSizeBytes = sizeBytes;
            succeeded = true;

            lock (_stateLock)
            {
                _lastRunSucceeded = true;
            }
        }
        catch (Exception ex)
        {
            succeeded = false;
            error = ex.Message;

            lock (_stateLock)
            {
                _lastRunSucceeded = false;
                _lastRunError = ex.Message;
            }
            throw; // the loop's catch logs it; the state is for the status card
        }
        finally
        {
            DateTime finishedUtc;
            lock (_stateLock)
            {
                _isRunning = false;
                finishedUtc = DateTime.UtcNow;
                _lastRunFinishedUtc = finishedUtc;
            }

            // Persist the completed run (success OR failure) to the
            // JSONL ledger. Inside the finally so both outcomes are
            // recorded; AppendRunHistoryRecordAsync swallows its own
            // IO failures — history must NEVER fail the run itself (and
            // a throw here in the finally would mask the original
            // backup exception).
            await AppendRunHistoryRecordAsync(directory, new BackupRunRecord
            {
                StartedUtc = startedUtc,
                FinishedUtc = finishedUtc,
                Succeeded = succeeded,
                Error = error,
                FileName = backupFileName,
                SizeBytes = backupFileSizeBytes,
                WasManual = wasManual
            });
        }
    }

    /// <summary>The pre-existing backup body, extracted so the run-state
    /// bookkeeping wraps it exactly once (scheduled AND manual runs share
    /// the same path). Returns the local file path + size in bytes for the
    /// run-history record.
    /// </summary>
    private async Task<(string LocalFilePath, long SizeBytes)> RunBackupCoreAsync(
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
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException ex) when (ex.Message.Contains("Cannot open backup device", StringComparison.Ordinal))
        {
            // SQL Server could not open the target file: the path does not
            // exist ON THE DB SERVER's filesystem, or its service account
            // cannot write there. Re-throw with the actionable diagnosis —
            // the raw engine message alone ("Operating system error 3")
            // gives the admin no hint about WHICH machine's filesystem is
            // at fault, and this message is what the Settings backup card
            // + run-history ledger surface.
            throw new InvalidOperationException(
                $"DatabaseBackup: SQL Server could not open the backup device '{serverFilePath}' " +
                $"({ex.Message}) The path must exist ON THE MACHINE RUNNING SQL SERVER and be writable by its " +
                "service account. Single-machine/dev: point DatabaseBackups:Directory at a folder the SQL Server " +
                "service account can write to (e.g. icacls \"C:\\TakOneBackups\" /grant " +
                "\"NT SERVICE\\MSSQL$INSTANCE:(OI)(CI)M\"), or set DatabaseBackups:ServerPath explicitly. " +
                "Docker: check the takone_backups volume is mounted at both DatabaseBackups:Directory (web) " +
                "and DatabaseBackups:ServerPath (db) — see DEPLOYMENT.md.",
                ex);
        }

        stopwatch.Stop();

        // Verify the file landed on the web-side mount. In Docker this is
        // the shared volume — a missing file means the volume wiring is
        // wrong (e.g. ServerPath doesn't point at the mount), which the
        // admin needs to know NOW, not at restore time.
        if (!File.Exists(localFilePath))
        {
            // Thrown (not logged+returned) so the run-state bookkeeping in
            // RunBackupAsync records it as a FAILED run — the status card
            // must show a wiring failure, not a green checkmark. The
            // loop's catch still logs it non-fatally.
            throw new InvalidOperationException(
                $"DatabaseBackup: BACKUP DATABASE reported success, but the file was not found at {localFilePath} " +
                "after the run. In the Docker deployment this means the takone_backups volume is not mounted at both " +
                "DatabaseBackups:Directory (web container) and DatabaseBackups:ServerPath (database container) — " +
                "check docker-compose.yml.");
        }

        var sizeBytes = new FileInfo(localFilePath).Length;
        var sizeMegabytes = sizeBytes / (1024.0 * 1024.0);

        _logger.LogInformation(
            "DatabaseBackup: {DatabaseName} backed up to {LocalFilePath} ({SizeMB:0.#} MB, {ElapsedSeconds:0.#}s).",
            databaseName, localFilePath, sizeMegabytes, stopwatch.Elapsed.TotalSeconds);

        return (localFilePath, sizeBytes);
    }

    // ── IBackupCoordinator (see the class remarks for the dual role) ──

    /// <summary>
    /// Snapshot of the backup subsystem: cached config (enabled/
    /// directory/retention), the guarded run-state fields, the next
    /// scheduled slot (recomputed from now — the fixed slots never shift),
    /// a NEWEST-FIRST listing of the <c>*.bak</c> files in the backup
    /// directory, and the persisted run-history ledger (newest first,
    /// capped at <see cref="MaxHistoryRecordsReturned"/>). NEVER returns
    /// null (see the interface's remarks).
    /// </summary>
    public BackupStatusDto GetStatus()
    {
        lock (_stateLock)
        {
            var directory = _directory;
            var enabled = _armed;

            // File listing — best-effort: an unreadable/missing directory
            // simply reports no files (the empty card state), never throws
            // into the query handler.
            var files = new List<BackupFileInfoDto>();
            long totalBytes = 0;
            try
            {
                if (Directory.Exists(directory))
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*.bak"))
                    {
                        var info = new FileInfo(file);
                        files.Add(new BackupFileInfoDto
                        {
                            FileName = info.Name,
                            SizeBytes = info.Length,
                            LastWriteUtc = info.LastWriteTimeUtc
                        });
                        totalBytes += info.Length;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Status must be resilient — a locked directory shows an
                // empty file list rather than a failed query.
                files = new List<BackupFileInfoDto>();
                totalBytes = 0;
            }

            files.Sort((a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));

            // Run-history ledger — best-effort read; a missing file (no
            // run has completed since the ledger shipped) or a torn tail
            // line (a concurrent append) yields whatever parses.
            var runHistory = ReadRunHistory(directory)
                .OrderByDescending(r => r.FinishedUtc)
                .Take(MaxHistoryRecordsReturned)
                .Select(r => new BackupRunRecordDto
                {
                    StartedUtc = r.StartedUtc,
                    FinishedUtc = r.FinishedUtc,
                    Succeeded = r.Succeeded,
                    Error = r.Error,
                    FileName = r.FileName,
                    SizeBytes = r.SizeBytes,
                    WasManual = r.WasManual
                })
                .ToList();

            return new BackupStatusDto
            {
                Enabled = enabled,
                IsRunning = _isRunning,
                LastRunStartedUtc = _lastRunStartedUtc,
                LastRunFinishedUtc = _lastRunFinishedUtc,
                LastRunSucceeded = _lastRunSucceeded,
                LastRunError = _lastRunError,
                NextScheduledRunUtc = enabled ? NextRunUtc(DateTime.UtcNow) : null,
                RetentionDays = _retentionDays,
                Files = files,
                TotalSizeBytes = totalBytes,
                RunHistory = runHistory
            };
        }
    }

    /// <summary>
    /// Accepts ONE pending manual trigger: sets the pending flag and
    /// completes the wake signal the schedule loop races its slot-delay
    /// against. Rejected with the concrete cause (per-reason contract —
    /// QA-5 handover item) when the schedule is disabled, a run is
    /// executing, or a trigger is already pending (one queued run at a
    /// time — the admin watches its progress on the status card).
    /// </summary>
    public BackupTriggerOutcome TryTriggerNow()
    {
        lock (_stateLock)
        {
            // Disabled / not-yet-armed is checked FIRST: without a live
            // schedule loop nothing would ever consume the wake signal,
            // so "disabled" must win over the transient run states.
            if (!_armed)
            {
                return BackupTriggerOutcome.RejectedDisabled;
            }

            if (_isRunning)
            {
                return BackupTriggerOutcome.RejectedInFlight;
            }

            if (_pendingTriggers > 0)
            {
                return BackupTriggerOutcome.RejectedPending;
            }

            _pendingTriggers = 1;
            _triggerSignal.TrySetResult(); // wakes the loop (idempotent)
            return BackupTriggerOutcome.Accepted;
        }
    }

    /// <summary>
    /// Resolves the DB-SERVER-side backup path used inside the BACKUP
    /// command. A RELATIVE value (the shipped default is
    /// <c>backups</c>) is anchored at the app's working directory — the
    /// SAME base <see cref="Directory.CreateDirectory(string)"/> uses
    /// for the web-side directory — so on a single-machine deployment
    /// the app and SQL Server address ONE physical folder.
    ///
    /// WHY ABSOLUTE IS REQUIRED: SQL Server resolves a relative
    /// <c>TO DISK</c> path against ITS OWN instance default backup
    /// folder (e.g. <c>C:\Program Files\Microsoft SQL Server\MSSQL17.HANZ\MSSQL\Backup\</c>),
    /// never the client's working directory — a relative
    /// <c>backups/…</c> therefore makes SQL Server look for
    /// <c>…\MSSQL\Backup\backups\…</c>, which does not exist, and
    /// every run fails with "Operating system error 3 (The system
    /// cannot find the path specified)".
    ///
    /// VERBATIM PASSTHROUGH: native absolute paths (rooted on this OS)
    /// and Windows drive-letter paths (<c>C:\…</c> — absolute on the
    /// DB server's filesystem even when the app itself runs on Linux
    /// against a remote Windows SQL Server) are returned unchanged;
    /// explicit absolute configuration always wins.
    /// INTERNAL for unit tests (DatabaseBackupServerPathTests).
    /// </summary>
    internal static string ResolveServerPath(string? configuredServerPath, string directory)
    {
        var serverPath = string.IsNullOrWhiteSpace(configuredServerPath)
            ? directory
            : configuredServerPath;

        if (IsWindowsDrivePath(serverPath) || Path.IsPathRooted(serverPath))
        {
            return serverPath;
        }

        return Path.GetFullPath(serverPath);
    }

    /// <summary>
    /// True for Windows drive-letter paths (<c>C:\backups</c> or
    /// <c>C:/backups</c>) — absolute on the DB server's filesystem even
    /// when the app itself runs on a different OS, so they must never
    /// be re-anchored by <see cref="Path.GetFullPath"/>.
    /// </summary>
    private static bool IsWindowsDrivePath(string value)
        => value.Length > 2
           && char.IsAsciiLetter(value[0])
           && value[1] == ':'
           && (value[2] == '\\' || value[2] == '/');

    /// <summary>
    /// Builds the BACKUP command. The database name and the disk path
    /// cannot be SQL parameters (they're identifiers/paths, not values),
    /// so they're embedded after <see cref="IsValidSqlIdentifier"/>
    /// validation — a strict allowlist that rules out injection through
    /// either the connection string's Initial Catalog or the configured
    /// server path. INTERNAL (not private) so the unit tests can
    /// exercise this security-relevant boundary directly.
    /// </summary>
    [SuppressMessage(
        "Microsoft.Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The database name and backup path are CONFIGURATION-sourced (connection string Initial Catalog + DatabaseBackups:ServerPath), not user input, and both are validated against strict allowlists (IsValidSqlIdentifier / IsValidBackupPath) before interpolation. BACKUP DATABASE's TO DISK argument cannot be a SQL parameter — it is a server-side file path, and parameterization of identifiers/paths is not supported by T-SQL. The allowlist validation is the security boundary here.")]
    internal static string BuildBackupCommandText(string databaseName, string serverFilePath)
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
    ///
    /// The COLON is allowed ONLY as a Windows drive-letter separator
    /// (second character, ASCII letter first — <c>C:\backups</c>). A
    /// colon cannot break out of the N'…' literal, but restricting it
    /// to the drive position keeps the allowlist tight now that
    /// absolute Windows paths reach the command (see
    /// <see cref="ResolveServerPath"/>). INTERNAL for unit tests.
    /// </summary>
    internal static bool IsValidBackupPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            var ok = (ch >= 'a' && ch <= 'z')
                     || (ch >= 'A' && ch <= 'Z')
                     || (ch >= '0' && ch <= '9')
                     || ch == '_' || ch == '-' || ch == '.'
                     || ch == '/' || ch == '\\'
                     // Windows drive-letter colon ("C:\…") — legal only
                     // at position 1, with an ASCII letter before it.
                     || (ch == ':' && index == 1 && char.IsAsciiLetter(value[0]));
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    // ── Run-history ledger (JSONL — see the class remarks) ──────────────

    /// <summary>
    /// One persisted run record — the on-disk shape. Mirrors the
    /// Application-layer <see cref="BackupRunRecordDto"/> (kept separate
    /// on purpose: the Infrastructure's storage format must be free to
    /// evolve without the DTO — and vice versa).
    /// </summary>
    private sealed record BackupRunRecord
    {
        public DateTime StartedUtc { get; init; }
        public DateTime FinishedUtc { get; init; }
        public bool Succeeded { get; init; }
        public string Error { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public bool WasManual { get; init; }
    }

    /// <summary>
    /// Appends one completed-run record as a single JSON line to the
    /// ledger. NEVER throws: history persistence is best-effort — a
    /// failed write (locked file, full disk, missing directory) is logged
    /// as a warning and the backup run's own outcome stands. Callers run
    /// this in a <c>finally</c>, where a throw would mask the original
    /// run outcome.
    /// </summary>
    private async Task AppendRunHistoryRecordAsync(string directory, BackupRunRecord record)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, HistoryFileName);
            var line = JsonSerializer.Serialize(record, HistoryJsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(path, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only IO failures are swallowed — anything else (a
            // serialization bug) should surface in the logs loud and clear.
            _logger.LogWarning(
                "DatabaseBackup: could not append run record to {HistoryFile}: {Exception}",
                HistoryFileName, ex);
        }
    }

    /// <summary>
    /// Parses the ledger into records, in FILE ORDER (oldest first —
    /// appends only). Best-effort: a missing file, an unreadable
    /// directory, or a malformed/torn line (e.g. a concurrent append
    /// mid-write) yields fewer-or-zero records — never a throw. The
    /// caller sorts + caps.
    /// </summary>
    private static List<BackupRunRecord> ReadRunHistory(string directory)
    {
        var records = new List<BackupRunRecord>();

        try
        {
            var path = Path.Combine(directory, HistoryFileName);
            if (!File.Exists(path))
            {
                return records; // no completed run has been recorded yet
            }

            // FileShare.ReadWrite: tolerate the loop's appender holding
            // the file open — a torn tail line simply fails to parse and
            // is skipped; the next line boundary resyncs.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new System.IO.StreamReader(stream);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    if (JsonSerializer.Deserialize<BackupRunRecord>(line, HistoryJsonOptions) is { } record)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // Torn/partial tail line or legacy junk — skip.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reads happen on coordinator threads while the loop may be
            // rewriting the file (prune) — treat any failure as "no
            // history available this poll"; the next read catches up.
            return new List<BackupRunRecord>();
        }

        return records;
    }

    /// <summary>
    /// Rewrites the ledger keeping only records that finished INSIDE the
    /// retention window (plus the defensive disk cap), oldest-first on
    /// disk. Atomic via temp-file + move-over so a concurrent reader sees
    /// either the old or the new file — never a half-written one. Called
    /// from the retention sweep on the loop thread; best-effort (a failed
    /// prune logs a warning and retries on the next sweep).
    /// </summary>
    private void PruneRunHistory(string directory, DateTime cutoffUtc)
    {
        try
        {
            var path = Path.Combine(directory, HistoryFileName);
            if (!File.Exists(path))
            {
                return;
            }

            var kept = ReadRunHistory(directory)
                .Where(r => r.FinishedUtc >= cutoffUtc)
                .OrderByDescending(r => r.FinishedUtc)
                .Take(MaxHistoryRecordsOnDisk)
                .ToList();

            var tempPath = path + ".tmp";
            using (var writer = new System.IO.StreamWriter(tempPath, append: false))
            {
                // Reverse back to oldest-first: the file is append-only,
                // so its natural order is chronological.
                for (var i = kept.Count - 1; i >= 0; i--)
                {
                    writer.WriteLine(JsonSerializer.Serialize(kept[i], HistoryJsonOptions));
                }
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                "DatabaseBackup: history prune failed for {HistoryFile}: {Exception}",
                HistoryFileName, ex);
        }
    }

    /// <summary>
    /// Deletes every <c>*.bak</c> in the backup directory older than the
    /// retention window (by last-write UTC). Runs on the web-side path —
    /// the same physical directory the database container wrote to via
    /// the shared volume. Also prunes the run-history ledger to the same
    /// window (see <see cref="PruneRunHistory"/>).
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

        // History records live exactly as long as the files they describe.
        PruneRunHistory(directory, cutoffUtc);

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

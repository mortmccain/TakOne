namespace TakOne.Application.Common.Models;

/// <summary>
/// Read-only snapshot of the scheduled-database-backup subsystem, surfaced
/// to admins on the Settings page (see <c>GetBackupStatusQuery</c>).
/// Deliberately contains NO paths — only what an admin needs to answer
/// "are backups healthy?" (last result, next slot, files, retention).
/// </summary>
public sealed record BackupStatusDto
{
    /// <summary>
    /// False when the schedule is disabled via configuration
    /// (<c>DatabaseBackups:Enabled=false</c>) or could not arm (no
    /// connection string). The UI renders an "disabled" notice instead of
    /// the status rows.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// True while a backup (scheduled or manually triggered) is executing.
    /// The UI polls while this is true so the admin sees the run finish.
    /// </summary>
    public bool IsRunning { get; init; }

    /// <summary>UTC start of the most recent run (scheduled or manual), null if never ran.</summary>
    public DateTime? LastRunStartedUtc { get; init; }

    /// <summary>UTC end of the most recent run, null if never ran or currently running.</summary>
    public DateTime? LastRunFinishedUtc { get; init; }

    /// <summary>
    /// Outcome of the most recent run: true = succeeded, false = failed,
    /// null = no run has completed yet.
    /// </summary>
    public bool? LastRunSucceeded { get; init; }

    /// <summary>
    /// Human-readable failure detail for the last run (empty on success /
    /// never-ran). Surfaces the same message the run logged.
    /// </summary>
    public string LastRunError { get; init; } = string.Empty;

    /// <summary>
    /// The next scheduled 03:30/14:30 UTC slot. Null when disabled. A
    /// manually-triggered run does NOT shift this — the schedule is fixed.
    /// </summary>
    public DateTime? NextScheduledRunUtc { get; init; }

    /// <summary>Configured retention window in days (files older than this are swept).</summary>
    public int RetentionDays { get; init; }

    /// <summary>
    /// Backup files currently in the backup directory, NEWEST FIRST.
    /// Bounded by the retention sweep (≤ 2/day × retention days).
    /// </summary>
    public IReadOnlyList<BackupFileInfoDto> Files { get; init; }
        = Array.Empty<BackupFileInfoDto>();

    /// <summary>Sum of <see cref="BackupFileInfoDto.SizeBytes"/> across <see cref="Files"/>.</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Recently COMPLETED backup runs (successes AND failures), NEWEST
    /// FIRST, persisted across restarts in a JSONL ledger next to the
    /// <c>.bak</c> files. Gives the admin a real audit trail ("did the
    /// 03:30 slot actually run last week?") instead of the in-memory
    /// last-run-only snapshot. Bounded by the retention window and capped
    /// at <c>50</c> entries — empty when no run has completed yet.
    /// </summary>
    public IReadOnlyList<BackupRunRecordDto> RunHistory { get; init; }
        = Array.Empty<BackupRunRecordDto>();
}

/// <summary>
/// One COMPLETED backup run (scheduled or manually triggered), persisted
/// in the run-history ledger (see <see cref="BackupStatusDto.RunHistory"/>).
/// </summary>
public sealed record BackupRunRecordDto
{
    /// <summary>UTC start of the run.</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>UTC end of the run.</summary>
    public DateTime FinishedUtc { get; init; }

    /// <summary>True when the backup file was written and verified; false = the run failed.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Failure detail (empty on success) — the same message the run logged.</summary>
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// The backup file's name (empty when the run failed before a file
    /// landed). File name only — paths are deployment internals.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>The backup file's size in bytes (0 when no file was produced).</summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// True when the run was triggered from the admin UI ("Back up now");
    /// false when it fired on the fixed 03:30/14:30 UTC schedule.
    /// </summary>
    public bool WasManual { get; init; }

    /// <summary>
    /// Non-failure annotation for the run (empty on normal runs). Carries
    /// the default-backup-directory fallback explanation when SQL Server
    /// could not write to the configured directory (the classic Windows
    /// dev box: the SQL service account has no write access to the app's
    /// backups folder) and the run nevertheless succeeded via the
    /// instance's own default backup directory. This is the ONE field
    /// where a full server-side path is deliberately surfaced — when the
    /// file could not be relocated into the backups folder, the admin
    /// needs to know exactly where it landed.
    /// </summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// One <c>*.bak</c> file in the backup directory (see
/// <see cref="BackupStatusDto.Files"/>).
/// </summary>
public sealed record BackupFileInfoDto
{
    /// <summary>File name only (no path) — paths are deployment internals.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>File size in bytes (raw data, formatted by the UI).</summary>
    public long SizeBytes { get; init; }

    /// <summary>UTC last-write time (when the backup was taken).</summary>
    public DateTime LastWriteUtc { get; init; }
}

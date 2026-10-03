using TakOne.Application.Common.Models;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Read/trigger port over the scheduled database-backup subsystem.
/// Application layer owns the abstraction; the Infrastructure layer's
/// <c>DatabaseBackupHostedService</c> implements it (the hosted service is
/// registered as a singleton and serves BOTH the IHostedService role and
/// this interface).
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A PORT (not the WebUI reading files/paths directly)</b>: the
/// backup schedule, run state, and file layout are Infrastructure
/// internals. Exposing them through an Application-owned interface keeps
/// the dependency direction intact (WebUI → Application ← Infrastructure)
/// and lets the Settings page consume a typed, path-free DTO.
/// </para>
/// <para>
/// <b>THREADING</b>: implementations must be safe to call from any thread —
/// the query handler runs on Wolverine's pipeline while the hosted loop
/// mutates the same state. The implementation guards its state with a
/// lock.
/// </para>
/// </remarks>
public interface IBackupCoordinator
{
    /// <summary>
    /// Returns a snapshot of the backup subsystem's state. NEVER returns
    /// null — a disabled / not-yet-armed coordinator reports
    /// <see cref="BackupStatusDto.Enabled"/> = false with empty run data.
    /// </summary>
    BackupStatusDto GetStatus();

    /// <summary>
    /// Requests a backup run as soon as possible (the hosted loop wakes,
    /// runs, and resumes its fixed schedule — a manual run never shifts
    /// the next scheduled slot).
    /// </summary>
    /// <returns>
    /// <see cref="BackupTriggerOutcome.Accepted"/> when the request was
    /// queued; otherwise the concrete rejection cause so the caller can
    /// surface an actionable message instead of one generic string.
    /// </returns>
    BackupTriggerOutcome TryTriggerNow();
}

/// <summary>
/// Outcome of <see cref="IBackupCoordinator.TryTriggerNow"/>. Replaced the
/// old boolean contract (QA-5 handover item: per-reason trigger
/// rejections) so the Settings card's toast can tell the admin WHICH
/// condition blocked the request instead of a generic "in progress or
/// disabled". The distinction lives in the Application layer because the
/// coordinator (Infrastructure) is the only component that can see its own
/// run state; the messages themselves are mapped in
/// <c>TriggerBackupNowCommandHandler</c>.
/// </summary>
public enum BackupTriggerOutcome
{
    /// <summary>
    /// The manual trigger was queued — the hosted loop wakes immediately,
    /// runs the backup on its own background execution, and resumes the
    /// fixed schedule afterwards.
    /// </summary>
    Accepted,

    /// <summary>A backup run is currently executing — retry once it finishes.</summary>
    RejectedInFlight,

    /// <summary>
    /// A manual trigger was already accepted and is still waiting to be
    /// picked up by the loop (typically mid-run when it was requested).
    /// </summary>
    RejectedPending,

    /// <summary>
    /// The backup schedule is disabled via configuration
    /// (<c>DatabaseBackups:Enabled=false</c>) or not yet armed (the brief
    /// window at host startup before the config block runs).
    /// </summary>
    RejectedDisabled
}

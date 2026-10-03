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
    /// True if the request was accepted; false when a run is already in
    /// flight or a trigger is already pending (the schedule is disabled
    /// entirely — <see cref="BackupStatusDto.Enabled"/> = false).
    /// </returns>
    bool TryTriggerNow();
}

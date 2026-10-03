using TakOne.Application.Common.Interfaces;
using TakOne.Application.Settings.Commands.TriggerBackupNow;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Settings.Commands.TriggerBackupNow;

/// <summary>
/// Delegates to <see cref="IBackupCoordinator.TryTriggerNow"/>. Each
/// rejection cause maps to its own message (QA-5 handover item: per-reason
/// trigger rejections) so the Settings card's toast tells the admin WHICH
/// condition blocked the request — same error contract as the CSV import
/// commands (plain <see cref="Result"/> failures with English messages;
/// the surrounding UI chrome is localized, the engine messages are not).
/// </summary>
public sealed class TriggerBackupNowCommandHandler
{
    public static Task<Result> HandleAsync(
        TriggerBackupNowCommand command,
        IBackupCoordinator backupCoordinator,
        CancellationToken cancellationToken)
    {
        // The coordinator is the ONLY component that can see its own run
        // state; the per-reason outcome it returns is mapped here, at the
        // Application boundary, into the message contract the UI consumes.
        var outcome = backupCoordinator.TryTriggerNow();

        if (outcome == BackupTriggerOutcome.Accepted)
        {
            return Task.FromResult(Result.Success());
        }

        var message = outcome switch
        {
            BackupTriggerOutcome.RejectedInFlight =>
                "A backup is already in progress — wait for it to finish, then try again.",
            BackupTriggerOutcome.RejectedPending =>
                "A manual backup has already been requested and is waiting to start.",
            _ => // RejectedDisabled (and any future cause — fail loud, not silent)
                "The backup schedule is disabled (DatabaseBackups:Enabled=false)."
        };

        return Task.FromResult(Result.Failure(message));
    }
}

using TakOne.Application.Common.Interfaces;
using TakOne.Application.Settings.Commands.TriggerBackupNow;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Settings.Commands.TriggerBackupNow;

/// <summary>
/// Delegates to <see cref="IBackupCoordinator.TryTriggerNow"/>. The
/// failure paths (a run already in flight / schedule disabled) map to
/// plain <see cref="Result"/> failures whose messages the Settings card
/// surfaces — same error contract as the CSV import commands.
/// </summary>
public sealed class TriggerBackupNowCommandHandler
{
    public static Task<Result> HandleAsync(
        TriggerBackupNowCommand command,
        IBackupCoordinator backupCoordinator,
        CancellationToken cancellationToken)
    {
        if (!backupCoordinator.TryTriggerNow())
        {
            // The coordinator rejects when a run is in flight, a trigger
            // is already pending, or the schedule is disabled — the
            // STATUS query tells the UI which (the message stays generic
            // because it cannot see the coordinator's internals).
            return Task.FromResult(Result.Failure(
                "A backup is already in progress or the backup schedule is disabled."));
        }

        return Task.FromResult(Result.Success());
    }
}

using TakOne.Application.Common.Interfaces;
using TakOne.Application.Common.Models;
using TakOne.Application.Settings.Queries.GetBackupStatus;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Settings.Queries.GetBackupStatus;

/// <summary>
/// Delegates to <see cref="IBackupCoordinator.GetStatus"/> — the handler
/// adds no logic of its own; it exists so the Wolverine pipeline applies
/// the query's authorization middleware and the WebUI gets a
/// <see cref="Result{T}"/> like every other query.
/// </summary>
public sealed class GetBackupStatusQueryHandler
{
    public static Task<Result<BackupStatusDto>> HandleAsync(
        GetBackupStatusQuery query,
        IBackupCoordinator backupCoordinator,
        CancellationToken cancellationToken)
    {
        // The coordinator NEVER returns null (see the interface's remarks);
        // a disabled subsystem reports Enabled=false, which the Settings
        // card renders as a "disabled" notice — not an error.
        var status = backupCoordinator.GetStatus();
        return Task.FromResult(Result<BackupStatusDto>.Success(status));
    }
}

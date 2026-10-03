using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Settings.Queries.GetBackupStatus;

/// <summary>
/// Returns the scheduled-database-backup subsystem's status snapshot
/// (last run + outcome, next scheduled slot, retention, file list) for the
/// Settings page's admin backup card.
///
/// AUTHORIZATION:
///   [RequireRoles(Admin)] — backup metadata (file names, sizes, run
///   times) is operational/infrastructure information; only IT
///   administrators should see it, mirroring the admin-only Settings
///   cards that consume it.
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed class GetBackupStatusQuery
{
    // No parameters — a full snapshot.
}

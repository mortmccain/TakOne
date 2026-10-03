using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Settings.Commands.TriggerBackupNow;

/// <summary>
/// Requests an immediate database-backup run (the Settings page's admin
/// backup card "Back up now" button). The command returns as soon as the
/// request is ACCEPTED — the backup itself runs on the hosted service's
/// own background execution, and the card follows its progress by polling
/// <c>GetBackupStatusQuery</c>.
///
/// AUTHORIZATION:
///   [RequireRoles(Admin)] — a backup is an infrastructure mutation (it
///   writes a file and takes a database lock); only IT administrators
///   may trigger one, same gate as reading the status.
///
/// IDEMPOTENCY:
///   Not applicable — a second command while a run is in flight FAILS
///   with a "backup already in progress" error rather than queueing
///   (the schedule is twice-daily by design; a queue would let one
///   admin stack runs back-to-back for no benefit).
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed class TriggerBackupNowCommand
{
    // No parameters.
}

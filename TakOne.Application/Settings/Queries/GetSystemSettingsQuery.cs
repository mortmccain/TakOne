using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Settings.Queries.GetSystemSettings;

/// <summary>
/// Returns the system-wide settings singleton (currently just LimitMode +
/// UpdatedAt). Used by the Settings page.
///
/// AUTHORIZATION:
///   Admin only — same as the SetSystemLimitMode command. The Settings
///   page is admin-only.
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed class GetSystemSettingsQuery
{
    // No parameters — returns the singleton.
}
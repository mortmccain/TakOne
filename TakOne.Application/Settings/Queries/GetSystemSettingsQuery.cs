using TakOne.Application.Common.Authorization;

namespace TakOne.Application.Settings.Queries.GetSystemSettings;

/// <summary>
/// Returns the system-wide settings singleton (currently just LimitMode +
/// UpdatedAt). Used by the ManageGroups page's limit-mode card.
///
/// AUTHORIZATION:
///   [RequireRoles(Admin, Manager, Employee)] — read access for all
///   ProductManagement staff. The ManageGroups page (policy:
///   ProductManagement = Admin/Manager/Employee) loads the current
///   LimitMode on page init to display it in the limit-mode card and to
///   seed the dropdown; only the SAVE button is admin-only (matching the
///   [RequireRoles(Admin)] gate on SetSystemLimitModeCommand, which the
///   page also hides for non-admins). LimitMode is non-sensitive
///   operational metadata (it drives purchase-limit enforcement UI), so
///   staff read access is by design.
/// </summary>
[RequireRoles(Roles.Admin, Roles.Manager, Roles.Employee)]
public sealed class GetSystemSettingsQuery
{
    // No parameters — returns the singleton.
}

using TakOne.Application.Common.Authorization;
using TakOne.Domain.Common.Enums;

namespace TakOne.Application.Settings.Commands.SetSystemLimitMode;

/// <summary>
/// Updates the system-wide LimitMode (CountOnly / SalaryOnly / Both).
///
/// AUTHORIZATION:
///   Admin only. Limit-mode changes affect the entire application's
///   purchase-limit enforcement — restricting to Admin prevents a
///   Manager from accidentally (or maliciously) disabling limits.
///
/// SEMANTICS:
///   - The new mode takes effect IMMEDIATELY — the cached
///     <c>ISystemSettingsService</c> entry is invalidated by this
///     command's handler, so the next read re-loads from DB.
///   - Switching from CountOnly to SalaryOnly (or Both) does NOT
///     retroactively apply salary-budget checks to existing draft carts.
///     The check is enforced on the NEXT cart mutation. So a customer
///     with an over-budget cart (built while mode was CountOnly) will
///     be blocked from adding more items, but won't be forced to remove
///     existing items.
/// </summary>
[RequireRoles(Roles.Admin)]
public sealed record SetSystemLimitModeCommand(LimitMode NewMode);
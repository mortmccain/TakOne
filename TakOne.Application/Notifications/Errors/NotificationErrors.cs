namespace TakOne.Application.Notifications.Errors;

/// <summary>
/// Stable, culture-neutral error codes for notification-related failures.
/// The UI layer (WebUI) intercepts these via TryParse + looks up the
/// localized message in the relevant resx file. Matches the existing
/// pattern in <c>TakOne.Application.Common.Errors.CartConflictErrors</c>,
/// <c>PurchaseLimitErrors</c>, <c>StockErrors</c>,
/// <c>SalaryBudgetExceededErrors</c>, <c>CategoryDeactivatedErrors</c>,
/// <c>NoCustomerGroupErrors</c>, <c>CurrencyMismatchErrors</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY STABLE CODES (NOT hardcoded English)</b>: leaking English error
/// strings to the UI breaks localization. The user's spec explicitly
/// mentions the Persian-mode toast leak as a regression we should not
/// repeat. Returning a stable code like "NotificationNotFound" lets the
/// UI layer localize per the user's CurrentUICulture.
/// </para>
/// <para>
/// <b>FORMAT</b>: the codes are short Pascal-case strings. They never
/// carry interpolated values (no <c>{0}</c>) — if a code needs params,
/// we add them as a separate stable-code-formatted-message protocol (see
/// <c>PurchaseLimitErrors.Format(productName, limit)</c>). Today's
/// notification errors don't need params.
/// </para>
/// </remarks>
public static class NotificationErrors
{
    /// <summary>
    /// The user attempted to mark a notification as read but is not
    /// authenticated. (Defense-in-depth — the [RequireAuthentication]
    /// attribute on the command should already reject this; the handler
    /// re-checks for non-HTTP hosts.)
    /// </summary>
    public static string FormatAuthRequired() => "NotificationAuthRequired";

    /// <summary>
    /// The notification the user tried to mark as read was either not
    /// found OR doesn't belong to them. Same UI message either way
    /// (anti-enumeration — don't leak "this notification belongs to
    /// someone else").
    /// </summary>
    public static string FormatNotFound() => "NotificationNotFound";

    // ── BROADCAST-SPECIFIC ERRORS ──────────────────────────────────────
    //
    // Used by SendBroadcastNotificationCommandHandler and
    // GetBroadcastNotificationsQueryHandler for clean, culture-neutral
    // error codes. Same convention as the existing Format*() methods
    // above (short Pascal-case strings, no interpolated values).

    /// <summary>
    /// A non-admin attempted to send a broadcast (defense-in-depth —
    /// the [RequireRoles(Admin)] attribute should already reject this).
    /// </summary>
    public static string FormatBroadcastAuthRequired() => "BroadcastAuthRequired";

    /// <summary>
    /// Scope=Group but the TargetGroupId doesn't correspond to any
    /// existing customer group (deleted between validation and fanout,
    /// or never existed).
    /// </summary>
    public static string FormatBroadcastGroupNotFound() => "BroadcastGroupNotFound";

    /// <summary>
    /// Scope=User but the TargetUserId doesn't correspond to any existing
    /// user (deleted between validation and fanout, or never existed).
    /// </summary>
    public static string FormatBroadcastUserNotFound() => "BroadcastUserNotFound";

    /// <summary>
    /// Scope=User but the TargetUserId refers to a user whose IsActive
    /// flag is false (soft-deactivated). The admin should target a
    /// different (active) user, or re-activate the target first.
    /// </summary>
    public static string FormatBroadcastUserInactive() => "BroadcastUserInactive";
}

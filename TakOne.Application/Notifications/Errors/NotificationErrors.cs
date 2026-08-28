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

    // ── APP-UPDATE BROADCAST VALIDATION (Brutal Code Review v3 #30, ──
    //    Round 18-C) ─────────────────────────────────────────────────
    //
    // The EmitAppUpdateBroadcastCommand is dispatched by the in-process
    // AppUpdateBroadcasterHostedService (marked [RequireSystemInternal] —
    // no human caller). The inputs are composed deterministically from
    // the running assembly version, so they're well-formed by
    // construction. BUT: defense-in-depth is the project's policy — a
    // future code path that exposes IMessageBus to a Blazor component
    // (or a developer manually composing the command for testing) could
    // supply arbitrarily long strings. Without length limits, a 100MB
    // Title or Message would be persisted to BroadcastNotification +
    // N fanout Notification rows — one row per active user — easily
    // exhausting DB storage or causing SQL Server's NVARCHAR(max) pages
    // to balloon.
    //
    // The limits enforced by EmitAppUpdateBroadcastCommandValidator +
    // the in-handler defense check are:
    //   - Title:    ≤ 200 chars  (matches SendBroadcastNotificationCommand)
    //   - Message:  ≤ 2000 chars (twice the admin command's 1000-char
    //                limit — app-update messages tend to include release
    //                notes / changelog snippets, so we allow more room).
    //
    // The error codes are short Pascal-case strings (no interpolated
    // values) — matches the convention of the rest of this file. The UI
    // layer intercepts these via TryParse + looks up the localized
    // message in the notification resx file.

    /// <summary>
    /// The Title on an EmitAppUpdateBroadcastCommand exceeded the 200-
    /// character limit. Returned by the in-handler defense check and
    /// by the FluentValidation validator. The system-internal caller
    /// (AppUpdateBroadcasterHostedService) composes the title from the
    /// assembly version, so this should never fire in production — it's
    /// a defense-in-depth guard against a future code path that exposes
    /// the command to external input.
    /// </summary>
    public static string FormatAppUpdateTitleTooLong() => "AppUpdateTitleTooLong";

    /// <summary>
    /// The Title on an EmitAppUpdateBroadcastCommand was empty or
    /// whitespace. Defense-in-depth guard — the system-internal caller
    /// (AppUpdateBroadcasterHostedService) always composes a non-empty
    /// title from the assembly version.
    /// </summary>
    public static string FormatAppUpdateTitleRequired() => "AppUpdateTitleRequired";

    /// <summary>
    /// The Message on an EmitAppUpdateBroadcastCommand exceeded the
    /// 2000-character limit. Same defense-in-depth rationale as
    /// <see cref="FormatAppUpdateTitleTooLong"/>.
    /// </summary>
    public static string FormatAppUpdateMessageTooLong() => "AppUpdateMessageTooLong";

    /// <summary>
    /// The Message on an EmitAppUpdateBroadcastCommand was empty or
    /// whitespace. Defense-in-depth guard — the system-internal caller
    /// always composes a non-empty message.
    /// </summary>
    public static string FormatAppUpdateMessageRequired() => "AppUpdateMessageRequired";
}

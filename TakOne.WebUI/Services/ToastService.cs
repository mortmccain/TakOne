using Radzen;

namespace TakOne.WebUI.Services;

/// <summary>
/// Thin wrapper around Radzen's <see cref="NotificationService"/>. Phase 0.15.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why wrap at all?</b> Three reasons:
/// </para>
/// <para>
/// 1) <b>Consistent message style.</b> Every toast across the app uses
///    the same severity → color → icon mapping. Centralizing it here
///    means a future redesign touches one file, not 16 page files.
/// </para>
/// <para>
/// 2) <b>Localization hook.</b> The i18n system is now in place
///    (<c>Resources/ValidationMessages.resx</c> + per-page resx files),
///    so we CAN route the <c>message</c> parameter through
///    <c>IStringLocalizer</c> here instead of at every call site — but we
///    haven't yet (toast messages are currently passed verbatim from the
///    call site). This is the single chokepoint for that future change.
/// </para>
/// <para>
/// 3) <b>Future audit hook.</b> If we ever need to log user-facing
///    notifications to the audit log, this is the single chokepoint.
/// </para>
/// <para>
/// <b>Unexpected-error shortcut.</b> <see cref="UnexpectedError"/>
/// is the canonical way to surface a catch-block surprise with the
/// user-facing reference code from
/// <see cref="TakOne.Application.Common.Errors.UnexpectedErrorCodes"/>.
/// The localized message + code are formatted by
/// <see cref="ErrorDisplayService"/>; call-sites stay one-liners.
/// </para>
/// <para>
/// <b>Usage from any razor page:</b>
/// <code>
/// @inject ToastService Toast
/// ...
/// await Toast.Success("Sale submitted.");
/// await Toast.Error("Could not approve sale: " + error);
/// await Toast.UnexpectedError(UnexpectedErrorCodes.Cart_UpdateFailure, summary: Loc["ErrorTitle"]);
/// </code>
/// </para>
/// </remarks>
public sealed class ToastService
{
    private readonly NotificationService _radzen;
    private readonly ErrorDisplayService _errorDisplay;

    public ToastService(NotificationService radzen, ErrorDisplayService errorDisplay)
    {
        _radzen = radzen;
        _errorDisplay = errorDisplay;
    }

    public Task Success(string message, string? summary = null, double durationMs = 3000)
        => Notify(NotificationSeverity.Success, summary ?? "Success", message, durationMs);

    public Task Info(string message, string? summary = null, double durationMs = 4000)
        => Notify(NotificationSeverity.Info, summary ?? "Info", message, durationMs);

    public Task Warning(string message, string? summary = null, double durationMs = 5000)
        => Notify(NotificationSeverity.Warning, summary ?? "Warning", message, durationMs);

    public Task Error(string message, string? summary = null, double durationMs = 6000)
        => Notify(NotificationSeverity.Error, summary ?? "Error", message, durationMs);

    /// <summary>
    /// Surface an UNEXPECTED error to the user. Formats the localized
    /// "unexpected error" message with the visible reference code
    /// (e.g. <c>"An unexpected error occurred. Error code: 47NQR83"</c>
    /// in en-US; the Persian counterpart in fa-IR). The 7-character
    /// code maps to a specific catch-block in the developer reference
    /// PDF — the support team looks up the code to pinpoint the file
    /// and remediation hint. Use this in every <c>catch (Exception)</c>
    /// block where the underlying exception is opaque.
    /// </summary>
    /// <param name="code">
    /// The 7-character opaque code from
    /// <see cref="TakOne.Application.Common.Errors.UnexpectedErrorCodes"/>.
    /// </param>
    /// <param name="summary">
    /// Optional toast-heading string. Defaults to the localized
    /// "Unexpected error" title from
    /// <see cref="ErrorDisplayService.UnexpectedTitle"/>.
    /// </param>
    /// <param name="durationMs">
    /// Optional override for the toast's duration in milliseconds.
    /// Unexpected errors stay on screen for 7s by default (vs. 6s for
    /// regular errors) so the user has time to copy the code.
    /// </param>
    public Task UnexpectedError(string code, string? summary = null, double durationMs = 7000)
        => Notify(NotificationSeverity.Error,
                  summary ?? _errorDisplay.UnexpectedTitle,
                  _errorDisplay.Unexpected(code),
                  durationMs);

    private Task Notify(NotificationSeverity severity, string summary, string detail, double durationMs)
    {
        // Radzen 11.1.1's NotificationMessage.Duration is `double?` and is
        // already in milliseconds — NOT a TimeSpan. (We pass our durationMs
        // straight through.)
        //
        // NotificationMessage has no `Position` property in this version —
        // the toast position is configured GLOBALLY on the NotificationService
        // when it's registered in Program.cs (see AddRadzenComponents +
        // services.Configure<NotificationOptions> there). Setting it per-
        // message isn't supported, so we don't try.
        _radzen.Notify(new NotificationMessage
        {
            Severity = severity,
            Summary = summary,
            Detail = detail,
            Duration = durationMs,
            CloseOnClick = true
        });

        return Task.CompletedTask;
    }
}

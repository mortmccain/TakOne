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
/// 2) <b>Localization hook.</b> When the i18n system is wired (Phase 0.17
///    resx files), we can route the <c>message</c> parameter through
///    <c>IStringLocalizer</c> here instead of at every call site.
/// </para>
/// <para>
/// 3) <b>Future audit hook.</b> If we ever need to log user-facing
///    notifications to the audit log, this is the single chokepoint.
/// </para>
/// <para>
/// <b>Usage from any razor page:</b>
/// <code>
/// @inject ToastService Toast
/// ...
/// await Toast.Success("Sale submitted.");
/// await Toast.Error("Could not approve sale: " + error);
/// </code>
/// </para>
/// </remarks>
public sealed class ToastService
{
    private readonly NotificationService _radzen;

    public ToastService(NotificationService radzen)
    {
        _radzen = radzen;
    }

    public Task Success(string message, string? summary = null, double durationMs = 3000)
        => Notify(NotificationSeverity.Success, summary ?? "Success", message, durationMs);

    public Task Info(string message, string? summary = null, double durationMs = 4000)
        => Notify(NotificationSeverity.Info, summary ?? "Info", message, durationMs);

    public Task Warning(string message, string? summary = null, double durationMs = 5000)
        => Notify(NotificationSeverity.Warning, summary ?? "Warning", message, durationMs);

    public Task Error(string message, string? summary = null, double durationMs = 6000)
        => Notify(NotificationSeverity.Error, summary ?? "Error", message, durationMs);

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
using System.Globalization;
using Microsoft.Extensions.Localization;
using TakOne.Application.Common.Errors;
using TakOne.Application.Resources;

namespace TakOne.WebUI.Services;

/// <summary>
/// Centralized service for formatting unexpected-error messages for the
/// UI layer. Wraps <see cref="IStringLocalizer{UnexpectedErrorMessages}"/>
/// and the <see cref="UnexpectedErrorCodes"/> catalog so call-sites
/// stay short and uniform.
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE.</b> This service is the single chokepoint for turning an
/// opaque 7-character code (e.g. <c>"47NQR83"</c>) into a fully
/// localized user-facing message:
/// <list type="bullet">
/// <item>EN → "An unexpected error occurred. Error code: 47NQR83"</item>
/// <item>fa-IR → "خطای غیرمنتظره‌ای رخ داد. کد خطا: 47NQR83"</item>
/// </list>
/// </para>
/// <para>
/// <b>WIRED THROUGHOUT THE UI.</b> Razor pages inject this service and
/// call <see cref="Unexpected"/> for catch-block toasts; the
/// <see cref="ToastService.UnexpectedError"/> method delegates here.
/// The Application layer does NOT call this service — it only emits
/// the wire-format prefix <c>"UE|"</c> + code in <c>Result.Failure</c>
/// strings; the UI recognizes that prefix in <see cref="Localize"/>
/// and routes through this service.
/// </para>
/// <para>
/// <b>LIFETIME</b>: <b>Scoped</b> — one instance per Blazor circuit.
/// The localizer is internally thread-safe; no further locking needed.
/// </para>
/// <para>
/// <b>WHY A SERVICE (vs. static helper)</b>: the existing
/// <c>XxxErrors.Format*()</c> stable-code helpers in
/// <c>TakOne.Application/Common/Errors</c> are static because they
/// return culture-neutral identifiers (e.g. "PurchaseLimitExceeded")
/// — the UI localizes them via per-page resx. Unexpected errors
/// cannot follow that pattern because the message must carry the
/// visible code AT format time, which requires the localizer's
/// <c>CurrentCulture</c> / <c>CurrentUICulture</c>. Hence this
/// DI service.
/// </para>
/// </remarks>
public sealed class ErrorDisplayService
{
    /// <summary>
    /// The wire-format prefix that Application-layer
    /// <c>Result.Failure</c> strings use to tag themselves as
    /// "unexpected" so the UI's <see cref="Localize"/> recognizer
    /// can route them through this service.
    /// </summary>
    /// <example>
    /// A backend catch block returns
    /// <c>Result.Failure($"UE|{UnexpectedErrorCodes.X}")</c>;
    /// the UI calls <c>ErrorDisplay.Localize(result.Error)</c> which
    /// detects the <c>"UE|"</c> prefix, strips it, and returns the
    /// localized "unexpected error" message with the visible code.
    /// </example>
    public const string WireFormatPrefix = "UE|";

    private readonly IStringLocalizer<UnexpectedErrorMessages> _localizer;

    public ErrorDisplayService(IStringLocalizer<UnexpectedErrorMessages> localizer)
    {
        _localizer = localizer;
    }

    /// <summary>
    /// Formats a fully-localized user-facing "unexpected error" message
    /// carrying the visible reference code. Uses the user's
    /// <c>CurrentUICulture</c> for the wrapper text; the code itself
    /// stays in Latin alphanumeric chars (NOT Persian digits) for
    /// copy-paste reliability into the support system.
    /// </summary>
    /// <param name="code">
    /// The 7-character opaque code from
    /// <see cref="UnexpectedErrorCodes"/> (e.g. <c>"47NQR83"</c>).
    /// </param>
    /// <returns>
    /// Localized message, e.g.
    /// "An unexpected error occurred. Error code: 47NQR83" (en-US) or
    /// "خطای غیرمنتظره‌ای رخ داد. کد خطا: 47NQR83" (fa-IR).
    /// </returns>
    public string Unexpected(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return string.Format(
            CultureInfo.CurrentUICulture,
            _localizer["Unexpected_Error_Format"],
            code);
    }

    /// <summary>
    /// Localized short toast-heading / alert-title for unexpected errors.
    /// </summary>
    public string UnexpectedTitle => _localizer["Unexpected_Error_Title"];

    /// <summary>
    /// The UI-side recognizer for Application-layer
    /// <c>Result.Failure("UE|{code}")</c> strings. If the input carries
    /// the <see cref="WireFormatPrefix"/>, this method strips the prefix
    /// and returns <see cref="Unexpected"/> with the extracted code.
    /// Otherwise, returns the input verbatim — the caller then falls
    /// back to its per-page <c>Loc["Error_Generic"]</c> or
    /// stable-code localization path.
    /// </summary>
    /// <param name="error">
    /// The raw <c>Result.Error</c> string from the Application layer.
    /// May be <c>null</c> (caller typically uses null-coalescing).
    /// </param>
    /// <returns>
    /// A localized "unexpected error" message (if input had the
    /// prefix); the input verbatim (if no prefix); or <c>null</c>
    /// (if input was null/empty).
    /// </returns>
    public string? Localize(string? error)
    {
        if (string.IsNullOrEmpty(error))
        {
            return null;
        }

        if (error.StartsWith(WireFormatPrefix, StringComparison.Ordinal))
        {
            var code = error[WireFormatPrefix.Length..];
            // Defensive: if the prefix is malformed, fall back to the
            // raw string (which won't look great but won't crash either).
            return code.Length >= 7
                ? Unexpected(code[..7])
                : Unexpected(code);
        }

        return error;
    }
}

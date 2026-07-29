using FluentValidation;

namespace TakOne.WebUI.Extensions;

/// <summary>
/// Extension methods for <see cref="ValidationException"/> used by the
/// WebUI layer to surface FluentValidation failures to the user.
/// </summary>
public static class ValidationExceptionExtensions
{
    /// <summary>
    /// Get all validation error messages from a
    /// <see cref="ValidationException"/>, joined with
    /// <see cref="Environment.NewLine"/> so the UI can render them as
    /// a multi-line error banner.
    ///
    /// Each error message is already localized (Phase 7 item D —
    /// TakOneLanguageManager resolves "@"-prefixed resource keys via
    /// IStringLocalizer&lt;ValidationMessages&gt; at the moment FluentValidation
    /// builds the message).
    ///
    /// Used by Blazor form handlers that dispatch commands via Wolverine:
    /// the generic catch block catches <c>Exception</c> and shows a generic
    /// "Could not create X" message, but a specific
    /// <c>catch (ValidationException vex)</c> block placed BEFORE the generic
    /// catch uses this method to surface the actual validation failures.
    /// </summary>
    public static string GetJoinedMessages(this ValidationException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var errors = ex.Errors?.ToList();
        if (errors is null || errors.Count == 0)
        {
            // Fall back to ex.Message if Errors collection is empty (shouldn't
            // happen with the default Wolverine FailureAction, but defensive).
            return ex.Message;
        }

        return string.Join(Environment.NewLine, errors.Select(e => e.ErrorMessage));
    }
}
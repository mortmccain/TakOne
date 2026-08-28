using FluentValidation;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Notifications.Errors;

namespace TakOne.Application.Notifications.Commands.EmitAppUpdateBroadcast;

/// <summary>
/// FluentValidation validator for <see cref="EmitAppUpdateBroadcastCommand"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A VALIDATOR FOR A SYSTEM-INTERNAL COMMAND?</b>
/// <c>EmitAppUpdateBroadcastCommand</c> is marked
/// <c>[RequireSystemInternal]</c> — the only caller is the in-process
/// <c>AppUpdateBroadcasterHostedService</c>, which composes the
/// <c>Title</c> and <c>Message</c> deterministically from the running
/// assembly version. The inputs are well-formed by construction, so this
/// validator should never fire in production.
/// </para>
/// <para>
/// <b>BUT</b> (Brutal Code Review v3 #30, Round 18-C): defense-in-depth
/// is the project's policy. A future code path that exposes
/// <c>IMessageBus</c> to a Blazor component (e.g. an admin UI for
/// "broadcast a custom message"), or a developer manually composing the
/// command for testing, could supply arbitrarily long strings. Without
/// length limits, a 100MB Title or Message would be persisted to
/// <c>BroadcastNotification</c> + N fanout <c>Notification</c> rows —
/// one row per active user — easily exhausting DB storage or causing
/// SQL Server's <c>NVARCHAR(max)</c> pages to balloon.
/// </para>
/// <para>
/// <b>LIMITS:</b>
/// <list type="bullet">
///   <item><c>Title</c>: ≤ 200 chars (matches
///   <c>SendBroadcastNotificationCommandValidator</c>'s limit on the
///   admin command — broadcast titles are short by design).</item>
///   <item><c>Message</c>: ≤ 2000 chars (twice the admin command's
///   1000-char limit — app-update messages tend to include release
///   notes / changelog snippets, so we allow more room).</item>
/// </list>
/// </para>
/// <para>
/// <b>PIPELINE INTEGRATION:</b> registered via
/// <c>services.AddValidatorsFromAssembly(...)</c> in
/// <c>TakOne.Application.DependencyInjection.ServiceCollectionExtensions</c>
/// and wired into Wolverine's middleware via
/// <c>opts.UseFluentValidation(RegistrationBehavior.ExplicitRegistration)</c>.
/// When the command is dispatched via Wolverine's <c>IMessageBus</c>,
/// the validator runs BEFORE the handler — validation failures
/// short-circuit the handler invocation entirely (Wolverine returns
/// the validation failures as the message result).
/// </para>
/// <para>
/// <b>DEFENSE-IN-DEPTH PAIRING:</b> the handler
/// (<see cref="EmitAppUpdateBroadcastCommandHandler"/>) ALSO checks
/// the same length limits at the top of <c>HandleAsync</c> and returns
/// <c>Result.Failure</c> with the same error codes. This double check
/// guards against a future caller that bypasses Wolverine's pipeline
/// (e.g. a direct handler invocation from a test or a non-Wolverine
/// dispatch path). See the handler's class-level XML doc for the
/// defense-in-depth rationale.
/// </para>
/// <para>
/// <b>ERROR CODES:</b> uses stable, culture-neutral codes from
/// <see cref="TakOne.Application.Notifications.Errors.NotificationErrors"/>:
/// <list type="bullet">
///   <item><c>AppUpdateTitleTooLong</c></item>
///   <item><c>AppUpdateMessageTooLong</c></item>
/// </list>
/// The UI layer localizes these via the notification resx file.
/// </para>
/// </remarks>
public sealed class EmitAppUpdateBroadcastCommandValidator
    : AbstractValidator<EmitAppUpdateBroadcastCommand>
{
    /// <summary>
    /// Maximum length (in characters) of the broadcast Title. Matches
    /// <c>SendBroadcastNotificationCommandValidator</c>'s limit.
    /// </summary>
    public const int MaxTitleLength = 200;

    /// <summary>
    /// Maximum length (in characters) of the broadcast Message. Twice
    /// <c>SendBroadcastNotificationCommandValidator</c>'s limit —
    /// app-update messages often include release notes / changelog
    /// snippets, which can run longer than a typical admin broadcast.
    /// </summary>
    public const int MaxMessageLength = 2000;

    public EmitAppUpdateBroadcastCommandValidator()
    {
        // Title: non-empty (the hosted service always composes a
        // non-empty title like "TakOne updated to vX.Y.Z") + ≤ 200 chars.
        RuleFor(x => x.Title)
            .NotEmpty().WithErrorCode(
                NotificationErrors.FormatAppUpdateTitleRequired())
            .MaximumLength(MaxTitleLength).WithErrorCode(
                NotificationErrors.FormatAppUpdateTitleTooLong());

        // Message: non-empty (the hosted service always composes a
        // non-empty message) + ≤ 2000 chars.
        RuleFor(x => x.Message)
            .NotEmpty().WithErrorCode(
                NotificationErrors.FormatAppUpdateMessageRequired())
            .MaximumLength(MaxMessageLength).WithErrorCode(
                NotificationErrors.FormatAppUpdateMessageTooLong());
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TakOne.WebUI.Services.Logging;

/// <summary>
/// The only sanctioned logger for the login flow. Replaces the 11 ad-hoc
/// <c>DIAG Login: ...</c> <c>LogWarning</c>/<c>LogError</c> calls that
/// leaked credential state and PII in Issue #03.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three-layer defense against PII / credential-state leakage:</b>
/// </para>
/// <list type="number">
///   <item>
///     <b>Allow-list by construction</b> — the only input shape is
///     <see cref="LoginLogContext"/>. Fields not on the record (Password,
///     PasswordLength, EmailConfirmed, LockoutEnd, AccessFailedCount,
///     Email, FullName, Gender) cannot be passed in. There is no
///     <c>params object[]</c> overload and no dictionary-bag escape hatch.
///   </item>
///   <item>
///     <b>Environment-gated diagnostics</b> — production logs ONLY the
///     audit-tier events (Success, Failure, LockedOut, Exception,
///     NoHttpContext). The Development tier adds Debug-level flow tracing,
///     but still never the forbidden fields (they cannot be passed in to
///     begin with — see point 1).
///   </item>
///   <item>
///     <b>CI analyzer</b> — the <c>TakOne.Analyzers.ForbiddenLoggingAnalyzer</c>
///     Roslyn analyzer flags any <c>ILogger.Log*</c> call whose format
///     string contains a forbidden token. Build severity is Error, so any
///     attempt to re-introduce the DIAG pattern fails CI immediately.
///   </item>
/// </list>
/// <para>
/// <b>Production log shape (always on, useful for SIEM):</b>
/// <list type="bullet">
///   <item>Success: <c>login.succeeded</c> @ Information with WorkerId + UserId + MustChangePassword</item>
///   <item>InvalidCredentials: <c>login.failed</c> @ Warning with WorkerId only</item>
///   <item>LockedOut: <c>login.locked_out</c> @ Warning with WorkerId only</item>
///   <item>Exception: <c>login.exception</c> @ Error with WorkerId + ExceptionTypeName only</item>
///   <item>NoHttpContext: <c>login.no_http_context</c> @ Error with WorkerId only</item>
/// </list>
/// </para>
/// <para>
/// <b>Development-only additions (gated by <see cref="IHostEnvironment.IsDevelopment"/>):</b>
/// <list type="bullet">
///   <item><c>login.flow_start</c> @ Debug with WorkerId — confirms the
///   form-submit handler fired. No Password, no PasswordLength.</item>
/// </list>
/// </para>
/// <para>
/// <b>Why WorkerId is allowed but PasswordLength is not:</b> WorkerId is
/// the username equivalent — any audit log of "who logged in" must record
/// it or the log is useless. PasswordLength, by contrast, is metadata
/// ABOUT the credential; logging it tells an attacker the password is
/// non-empty and roughly how long it is, which is useful for brute-force
/// prioritization. The line is "is this field the identifier the user is
/// claiming to be, or is it metadata about a secret?" Identifiers may be
/// logged; secret-metadata may not.
/// </para>
/// <para>
/// <b>Why <c>SignInResult</c> branch is NOT logged:</b> the original DIAG
/// calls logged <c>Succeeded={Succeeded}, IsLockedOut={Locked},
/// IsNotAllowed={NotAllowed}, Requires2FA={TwoFA}</c>. That tells an
/// attacker which accounts are locked, which have unconfirmed emails,
/// which have 2FA enabled — exactly the intelligence needed for
/// credential-stuffing. We collapse everything except Success and
/// LockedOut into <see cref="LoginLogOutcome.InvalidCredentials"/> so the
/// audit log reveals nothing beyond "the credentials were rejected".
/// </para>
/// <para>
/// <b>Lifetime:</b> Scoped — depends on <c>IHostEnvironment</c> (singleton)
/// and <c>ILogger&lt;LoginAuditLogger&gt;</c> (singleton). Could be
/// Singleton, but Scoped matches the convention used by other WebUI
/// services (<c>BlazorCurrentUserService</c>, <c>ToastService</c>) and
/// costs nothing.
/// </para>
/// </remarks>
public sealed class LoginAuditLogger
{
    /// <summary>
    /// The set of event names emitted by this logger. Surfaced as a public
    /// constant so SIEM queries / log dashboards can reference them without
    /// magic strings.
    /// </summary>
    public static class EventNames
    {
        public const string FlowStart = "login.flow_start";
        public const string Succeeded = "login.succeeded";
        public const string Failed = "login.failed";
        public const string LockedOut = "login.locked_out";
        public const string Exception = "login.exception";
        public const string NoHttpContext = "login.no_http_context";
    }

    private readonly ILogger<LoginAuditLogger> _logger;
    private readonly IHostEnvironment _environment;

    public LoginAuditLogger(
        ILogger<LoginAuditLogger> logger,
        IHostEnvironment environment)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary>
    /// Development-only flow-start trace. Logged at Debug — so it disappears
    /// entirely in Production (where the default log level is Information
    /// per appsettings.json). Gated by IsDevelopment as a second layer of
    /// defense: even if a developer accidentally lowers the Production log
    /// level to Debug, this method still no-ops.
    /// </summary>
    public void LogFlowStart(string workerId)
    {
        if (!_environment.IsDevelopment())
        {
            return;
        }

        _logger.LogDebug(
            "{EventName} WorkerId='{WorkerId}'",
            EventNames.FlowStart,
            workerId);
    }

    /// <summary>
    /// Emits the appropriate audit event for the given context. Called by
    /// the login page at every exit point of the sign-in flow.
    /// </summary>
    public void Log(in LoginLogContext context)
    {
        switch (context.Outcome)
        {
            case LoginLogOutcome.Success:
                _logger.LogInformation(
                    "{EventName} WorkerId='{WorkerId}' UserId='{UserId}' MustChangePassword={MustChangePassword}",
                    EventNames.Succeeded,
                    context.WorkerId,
                    context.UserId,
                    context.MustChangePassword);
                break;

            case LoginLogOutcome.InvalidCredentials:
                // WARNING — surfaces in default Production log level. WorkerId
                // only, no UserId (attacker submitting random WorkerIds must
                // not learn which ones resolve to valid Guids).
                _logger.LogWarning(
                    "{EventName} WorkerId='{WorkerId}'",
                    EventNames.Failed,
                    context.WorkerId);
                break;

            case LoginLogOutcome.LockedOut:
                // Distinct from InvalidCredentials so SIEM can correlate
                // brute-force attacks. LockoutEnd is NEVER logged.
                _logger.LogWarning(
                    "{EventName} WorkerId='{WorkerId}'",
                    EventNames.LockedOut,
                    context.WorkerId);
                break;

            case LoginLogOutcome.Exception:
                // Exception type name only — message may contain PII,
                // stack trace leaks internal structure. The full exception
                // is available in the dev's local debugger; production
                // logs get only the classification.
                _logger.LogError(
                    "{EventName} WorkerId='{WorkerId}' ExceptionType='{ExceptionType}'",
                    EventNames.Exception,
                    context.WorkerId,
                    context.ExceptionTypeName ?? "Unknown");
                break;

            case LoginLogOutcome.NoHttpContext:
                _logger.LogError(
                    "{EventName} WorkerId='{WorkerId}'",
                    EventNames.NoHttpContext,
                    context.WorkerId);
                break;

            default:
                // Defensive — if a new outcome is added to the enum without
                // updating this switch, fall back to the safe failure log
                // rather than silently dropping the event.
                _logger.LogWarning(
                    "{EventName} WorkerId='{WorkerId}' Outcome='{Outcome}'",
                    EventNames.Failed,
                    context.WorkerId,
                    context.Outcome);
                break;
        }
    }
}
namespace TakOne.WebUI.Services.Logging;

/// <summary>
/// The allow-list of fields that may be logged by <see cref="LoginAuditLogger"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>THIS RECORD IS THE ALLOW-LIST.</b> The <see cref="LoginAuditLogger"/>
/// accepts <c>LoginLogContext</c> as its only input shape. Fields not present
/// on this record <b>cannot</b> be logged by the audit logger — there is no
/// overload, no <c>params object[]</c> escape hatch, no <c>Dictionary</c>
/// bag. This is defense-by-construction: a developer cannot accidentally add
/// a new field to a log call without first adding it to this record, which
/// forces a code review of the allow-list.
/// </para>
/// <para>
/// <b>Forbidden fields (Issue #03) — deliberately absent:</b>
/// <list type="bullet">
///   <item><c>Password</c> — the credential itself</item>
///   <item><c>PasswordLength</c> — tells the attacker the password is non-empty and how long it is (useful for brute-force prioritization)</item>
///   <item><c>EmailConfirmed</c> — account-state intelligence (which accounts are fully provisioned vs. pending)</item>
///   <item><c>LockoutEnd</c> — lockout-state intelligence (tells attacker exactly when to retry)</item>
///   <item><c>AccessFailedCount</c> — tells attacker how many more attempts before lockout triggers</item>
///   <item><c>Email</c> — PII, and Identity doesn't use it for our sign-in flow anyway</item>
///   <item><c>FullName</c> — PII, not needed for security audit</item>
///   <item><c>Gender</c> — PII, not needed for security audit</item>
/// </list>
/// </para>
/// <para>
/// <b>Allowed fields:</b>
/// <list type="bullet">
///   <item><see cref="WorkerId"/> — the user-supplied identifier at the
///   login form. NOT a secret — it's the username equivalent. Required for
///   any useful audit log (correlating "who tried to log in" with "who
///   succeeded" with "who got locked out").</item>
///   <item><see cref="Outcome"/> — see <see cref="LoginLogOutcome"/> for the
///   deliberate collapse of multiple Identity states into one bucket.</item>
///   <item><see cref="UserId"/> — the <see cref="Guid"/> PK from
///   <c>AspNetUsers</c>. Logged only on Success (so downstream SIEM can
///   correlate post-login activity with the user row). Never logged on
///   InvalidCredentials (an attacker submitting random WorkerIds would
///   otherwise learn which ones resolve to valid Guids).</item>
///   <item><see cref="ExceptionTypeName"/> — the short type name of an
///   unexpected exception (e.g. <c>SqlException</c>,
///   <c>InvalidOperationException</c>). NOT the message (which may contain
///   PII) and NOT the stack trace (which leaks internal structure).</item>
///   <item><see cref="MustChangePassword"/> — boolean, logged only on
///   Success, indicates the user was redirected to <c>/Account/ChangePassword</c>.
///   Useful for tracking Issue #02 forced-password-change compliance.</item>
/// </list>
/// </para>
/// <para>
/// <b>CI ENFORCEMENT:</b> The <c>TakOne.Analyzers</c> project ships a
/// Roslyn analyzer (<c>ForbiddenLoggingAnalyzer</c>) that flags any call to
/// <c>ILogger.Log*</c> whose format string contains a forbidden token
/// (<c>"DIAG Login"</c>, <c>"PasswordLength"</c>, <c>"EmailConfirmed"</c>,
/// <c>"LockoutEnd"</c>, <c>"AccessFailedCount"</c>, <c>"Password="</c>).
/// Build-time severity is Error, so any leak attempt fails CI.
/// </para>
/// </remarks>
public sealed record LoginLogContext
{
    /// <summary>
    /// The user-supplied identifier from the login form. Required.
    /// </summary>
    public required string WorkerId { get; init; }

    /// <summary>
    /// The outcome of the login attempt. Required.
    /// </summary>
    public required LoginLogOutcome Outcome { get; init; }

    /// <summary>
    /// The <c>AspNetUsers.Id</c> Guid. Optional — set only on Success and
    /// only when the application actually has the user row in hand. NEVER
    /// set on InvalidCredentials outcomes.
    /// </summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// Short type name of an unexpected exception. Optional — set only on
    /// the <see cref="LoginLogOutcome.Exception"/> outcome.
    /// </summary>
    public string? ExceptionTypeName { get; init; }

    /// <summary>
    /// True if the user was redirected to <c>/Account/ChangePassword</c>
    /// after a successful sign-in (Issue #02 forced-password-change flow).
    /// Optional — set only on Success.
    /// </summary>
    public bool MustChangePassword { get; init; }
}

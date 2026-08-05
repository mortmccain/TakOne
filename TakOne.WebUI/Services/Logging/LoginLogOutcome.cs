namespace TakOne.WebUI.Services.Logging;

/// <summary>
/// Coarse-grained outcome of a login attempt. Used as the ONLY classification
/// logged by <see cref="LoginAuditLogger"/> in any environment.
/// </summary>
/// <remarks>
/// <para>
/// <b>SECURITY INVARIANT — DO NOT ADD FINE-GRAINED VALUES.</b>
/// </para>
/// <para>
/// The original (Issue #03) leak came from logging the exact
/// <c>SignInResult</c> branch (<c>Succeeded</c>, <c>IsLockedOut</c>,
/// <c>IsNotAllowed</c>, <c>RequiresTwoFactor</c>, fall-through) at WARNING
/// level. That told anyone with log access exactly which accounts were
/// locked, which had unconfirmed emails, which had 2FA enabled — a
/// credential-stuffing intelligence goldmine.
/// </para>
/// <para>
/// This enum deliberately collapses several Identity outcomes into a single
/// <see cref="InvalidCredentials"/> bucket:
/// <list type="bullet">
///   <item><c>SignInResult.Failed</c> (wrong password)</item>
///   <item><c>SignInResult.NotAllowed</c> (email not confirmed — never fires under our config but is handled defensively)</item>
///   <item><c>SignInResult.RequiresTwoFactor</c> (2FA not implemented in v1)</item>
///   <item>Pre-check failure: user not found OR <c>IsActive=false</c></item>
/// </list>
/// All four return the same <c>InvalidCredentials</c> outcome and the same
/// user-facing error message. The audit log gets ONE fact ("login failed for
/// WorkerId X with reason InvalidCredentials") — nothing more.
/// </para>
/// <para>
/// <see cref="LockedOut"/> is kept as a distinct value because lockout is an
/// account-protection event, not a credential-validation event — security
/// operations needs to see lockouts in the SIEM to correlate brute-force
/// attempts. The locked-out user's <c>LockoutEnd</c> timestamp, however, is
/// NEVER logged (it tells the attacker exactly when to retry).
/// </para>
/// </remarks>
public enum LoginLogOutcome
{
    /// <summary>
    /// Credentials were rejected OR the user account is in a state that
    /// prevents sign-in (not found, deactivated, email unconfirmed, 2FA
    /// required). Deliberately coarse-grained — see class remarks.
    /// </summary>
    InvalidCredentials,

    /// <summary>
    /// Account is currently locked out due to too many failed attempts.
    /// Distinct from InvalidCredentials so SIEM can correlate brute-force
    /// attacks. <c>LockoutEnd</c> is NEVER logged.
    /// </summary>
    LockedOut,

    /// <summary>
    /// Sign-in succeeded and the auth cookie was issued. Logged at
    /// Information in all environments.
    /// </summary>
    Success,

    /// <summary>
    /// An unexpected exception was thrown during the login flow. The
    /// exception type name is logged; the message and stack trace are NOT
    /// (they may contain PII or configuration details).
    /// </summary>
    Exception,

    /// <summary>
    /// The login flow could not run because <c>HttpContext</c> was null.
    /// Defensive only — should never fire in normal operation. Logged at
    /// Error in all environments so it surfaces if it ever does.
    /// </summary>
    NoHttpContext,
}
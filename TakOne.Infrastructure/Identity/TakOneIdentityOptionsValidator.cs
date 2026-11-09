using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace TakOne.Infrastructure.Identity;

/// <summary>
/// Startup-time fail-fast validator for ASP.NET Identity configuration.
///
/// WHY THIS EXISTS:
///   The Brutal Code Review v3 (finding #01) flagged a structural config-
///   binding bug that persisted across THREE review cycles:
///   <c>appsettings.json</c> nested <c>Identity</c>/<c>Auth</c>/
///   <c>DefaultAdmin</c> under <c>TakOne.Database.*</c>, but the binding
///   code read them as <c>TakOne:Identity</c> (siblings of Database).
///   The operator's configured password policy, lockout window, and
///   RequireUniqueEmail were SILENTLY IGNORED in Production — ASP.NET
///   Identity defaults took over. The bug was invisible because no error
///   fired; the defaults happen to be safer in some dimensions (lockout
///   attempts) and less safe in others (RequireUniqueEmail=false).
///
///   This validator makes a re-introduction of the binding bug LOUD:
///   if the bound <c>IdentityOptions</c> do not meet our security policy
///   (RequiredLength ≥ 8, MaxFailedAccessAttempts ≤ 50,
///   ResetLockoutOnSuccess switch enabled), startup throws
///   <see cref="OptionsValidationException"/> BEFORE the app starts
///   serving traffic. The key insight: if the config-binding path is
///   wrong, ASP.NET Identity DEFAULTS take over (RequiredLength=6),
///   which FAILS this validator — so a broken binding cannot boot
///   silently.
///
/// VALIDATION INVARIANTS (security policy — not arbitrary):
///   - <c>Password.RequiredLength</c> ≥ 8  (OWASP minimum; Identity default is 6)
///   - <c>Password.RequireNonAlphanumeric</c> = true (mitigates dictionary attacks)
///   - <c>Password.RequireUppercase</c> = true
///   - <c>Password.RequireLowercase</c> = true
///   - <c>Password.RequireDigit</c> = true
///   - <c>Lockout.MaxFailedAccessAttempts</c> ≤ 50 (owner-approved
///     maximum — see the LOCKOUT POLICY note below; anything higher is a
///     fat-finger value or a re-introduced broken config)
///   - The <c>Microsoft.AspNetCore.Identity.CheckPasswordSignInAlwaysResetLockoutOnSuccess</c>
///     AppContext switch must be ENABLED (set by <c>AddTakOneInfrastructure</c>)
///     — the structural mitigation that makes a 50-attempt threshold safe
///   - <c>Lockout.AllowedForNewUsers</c> = true (lockout must apply during
///     the initial login attempts, not just after the first success)
///
/// LOCKOUT POLICY (owner decision, QA-18):
///   History: the Brutal Code Review v3 (Sep 19) capped attempts at 10;
///   the owner's Sep 23 Production override of 50 then crashed the first
///   Production deploy against that fail-fast ceiling (d185956 lowered
///   the override to 10 to unblock the deploy). The owner has now
///   CONSCIOUSLY selected 50 for Production: the workforce skews elderly
///   and tighter thresholds locked out legitimate users. Two conditions
///   make that acceptable, and BOTH are enforced here:
///     1. MaxFailedAccessAttempts ≤ 50 — the ceiling still catches
///        misconfigurations (a stray 500) and re-introduced broken
///        configs; 50 is the signed-off maximum, not a slippery slope.
///     2. Lockout counters reset on every successful login. In .NET 10
///        this is the AppContext switch
///        <c>Microsoft.AspNetCore.Identity.CheckPasswordSignInAlwaysResetLockoutOnSuccess</c>
///        (verified against the 10.0 reference source: without it,
///        <c>SignInManager.CheckPasswordSignInCoreAsync</c> SKIPS the
///        reset for users with two-factor enabled whose client is not
///        remembered — so any future 2FA/passkey adoption would silently
///        reintroduce cross-day failure accumulation). NOTE: this is NOT
///        a <c>LockoutOptions</c> property — there is no config key for
///        it; <c>AddTakOneInfrastructure</c> sets the switch at composition
///        time and this validator fails fast if a host ever wires
///        Identity without it.
///   With reset-on-success, a lockout requires 50 CONSECUTIVE failures —
///   attacker behavior, not typo behavior. The residual brute-force cost
///   of 50 attempts per 15-minute window is a deliberate owner-accepted
///   trade-off; revisit this policy only with the owner, not by editing
///   config alone.
///
/// EMAIL POLICY (UPDATED):
///   We intentionally allow <c>User.RequireUniqueEmail = false</c>. The
///   TakOne app authenticates users via their <c>WorkerId</c> (which
///   becomes <c>ApplicationUser.UserName</c>), NOT via email. None of
///   the create-user flows collect an email address — staff and customers
///   don't have company emails, and the password-reset flow is admin-
///   driven, not user-self-service-via-email. Forcing
///   <c>RequireUniqueEmail = true</c> made every create-user call fail
///   in <c>UserManager.CreateAsync</c> with an "Email is required" /
///   "InvalidEmail" IdentityError because the empty/null email passed
///   in from the command handlers failed Identity's email validator.
///   By leaving <c>RequireUniqueEmail = false</c> (the ASP.NET Identity
///   default), accounts with no email are accepted, and accounts that
///   DO carry an email (e.g. the bootstrap admin) are still subject
///   to Identity's email format validation.
///
/// HOW IT WIRES UP:
///   Registered in <see cref="DependencyInjection.ServiceCollectionExtensions"/>
///   via <c>services.AddSingleton&lt;IValidateOptions&lt;IdentityOptions&gt;,
///   TakOneIdentityOptionsValidator&gt;()</c>. ASP.NET Core's
///   <c>OptionsValidationPostConfigure</c> runs all registered
///   <c>IValidateOptions&lt;T&gt;</c> implementations the first time an
///   <c>IOptions&lt;T&gt;</c> value is resolved — typically during the
///   first authenticated request or at explicit
///   <c>IOptionsMonitor&lt;T&gt;.CurrentValue</c> access.
/// </summary>
internal sealed class TakOneIdentityOptionsValidator : IValidateOptions<IdentityOptions>
{
    /// <summary>
    /// Validates the bound <see cref="IdentityOptions"/> against our
    /// security policy. Returns <see cref="ValidateOptionsResult.Skip"/>
    /// (no named-options match) when the options name is not the default
    /// — we only validate the default <c>IdentityOptions</c> instance.
    /// </summary>
    public ValidateOptionsResult Validate(string? name, IdentityOptions options)
    {
        // Only validate the default (unnamed) IdentityOptions instance.
        // Named options are rare for Identity but supported by the options
        // system; skip them to avoid false positives.
        if (!string.IsNullOrEmpty(name))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();

        // ── Password policy ──
        // These checks catch a broken config binding: ASP.NET Identity's
        // defaults are RequiredLength=6, RequireNonAlphanumeric=true,
        // RequireUppercase=true, RequireLowercase=true, RequireDigit=true.
        // Our policy requires RequiredLength ≥ 8 — so a broken binding
        // (defaults take over) fails this check LOUDLY at startup.
        if (options.Password.RequiredLength < 8)
        {
            failures.Add(
                "TakOne:Identity:Password:RequiredLength must be ≥ 8 (OWASP minimum). " +
                $"Bound value: {options.Password.RequiredLength}. If this is 6, the " +
                "config-binding path is wrong — Identity options are NOT reaching the " +
                "bound instance. Check that appsettings.json places 'Identity' as a " +
                "sibling of 'TakOne:Database' (NOT nested under it). " +
                "See Brutal Code Review v3 finding #01.");
        }

        if (!options.Password.RequireNonAlphanumeric)
        {
            failures.Add("TakOne:Identity:Password:RequireNonAlphanumeric must be true.");
        }

        if (!options.Password.RequireUppercase)
        {
            failures.Add("TakOne:Identity:Password:RequireUppercase must be true.");
        }

        if (!options.Password.RequireLowercase)
        {
            failures.Add("TakOne:Identity:Password:RequireLowercase must be true.");
        }

        if (!options.Password.RequireDigit)
        {
            failures.Add("TakOne:Identity:Password:RequireDigit must be true.");
        }

        // ── Lockout policy ──
        // ASP.NET Identity default is MaxFailedAccessAttempts=5. The
        // owner has consciously selected 50 for Production (elderly
        // workforce — see the class-level LOCKOUT POLICY note). The
        // ceiling therefore sits at the owner-approved 50: anything
        // higher is a fat-finger value or a re-introduced broken config
        // and fails LOUDLY here at startup.
        if (options.Lockout.MaxFailedAccessAttempts > 50)
        {
            failures.Add(
                "TakOne:Identity:Lockout:MaxFailedAccessAttempts must be ≤ 50 " +
                $"(owner-approved maximum — see TakOneIdentityOptionsValidator's LOCKOUT POLICY note). " +
                $"Bound value: {options.Lockout.MaxFailedAccessAttempts}.");
        }

        // ResetLockoutOnSuccess is the structural mitigation that makes a
        // 50-attempt threshold acceptable. In .NET 10 it is NOT a
        // LockoutOptions property — it is the AppContext switch
        // "Microsoft.AspNetCore.Identity.CheckPasswordSignInAlwaysResetLockoutOnSuccess"
        // (verified against the 10.0 reference source: SignInManager.
        // CheckPasswordSignInCoreAsync resets the failed-attempt counter
        // after a successful password check only when the switch is
        // enabled — without it, users with two-factor enabled whose
        // client is not remembered keep accumulating failures ACROSS
        // DAYS). AddTakOneInfrastructure sets the switch at composition
        // time; this check fails fast if a host ever wires Identity
        // without it, so the pairing "50 attempts ⇒ reset-on-success"
        // cannot be silently broken.
        if (!AppContext.TryGetSwitch(
                "Microsoft.AspNetCore.Identity.CheckPasswordSignInAlwaysResetLockoutOnSuccess",
                out var resetLockoutOnSuccessEnabled)
            || !resetLockoutOnSuccessEnabled)
        {
            failures.Add(
                "The AppContext switch 'Microsoft.AspNetCore.Identity." +
                "CheckPasswordSignInAlwaysResetLockoutOnSuccess' must be enabled " +
                "(AddTakOneInfrastructure sets it at composition time) — lockout " +
                "counters must reset on every successful login. Without it, " +
                "non-consecutive failures accumulate across days and lock out " +
                "legitimate users; it is the required companion of the " +
                "owner-approved MaxFailedAccessAttempts=50 policy.");
        }

        if (!options.Lockout.AllowedForNewUsers)
        {
            failures.Add(
                "TakOne:Identity:Lockout:AllowedForNewUsers must be true — lockout " +
                "must apply during initial login attempts.");
        }

        // ── User policy ──
        // ASP.NET Identity default is RequireUniqueEmail=false. We DO NOT
        // enforce RequireUniqueEmail=true here — TakOne authenticates users
        // via WorkerId (UserName), NOT email, and the create-user flows do
        // not collect an email address. Forcing RequireUniqueEmail=true
        // made every create-user call fail with "Email is required" /
        // "InvalidEmail" because the empty/null email passed by the
        // command handlers failed Identity's email validator. We leave
        // RequireUniqueEmail=false (the ASP.NET default) so accounts with
        // no email are accepted. Accounts that DO carry an email (e.g.
        // the bootstrap admin) are still subject to Identity's email
        // format validation.
        // See the class-level EMAIL POLICY remark for the full rationale.

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

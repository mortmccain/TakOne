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
///   (RequiredLength ≥ 8, MaxFailedAccessAttempts ≤ 10, RequireUniqueEmail
///   = true), startup throws <see cref="OptionsValidationException"/>
///   BEFORE the app starts serving traffic. The key insight: if the
///   config-binding path is wrong, ASP.NET Identity DEFAULTS take over
///   (RequiredLength=6, RequireUniqueEmail=false), which FAIL this
///   validator — so a broken binding cannot boot silently.
///
/// VALIDATION INVARIANTS (security policy — not arbitrary):
///   - <c>Password.RequiredLength</c> ≥ 8  (OWASP minimum; Identity default is 6)
///   - <c>Password.RequireNonAlphanumeric</c> = true (mitigates dictionary attacks)
///   - <c>Password.RequireUppercase</c> = true
///   - <c>Password.RequireLowercase</c> = true
///   - <c>Password.RequireDigit</c> = true
///   - <c>Lockout.MaxFailedAccessAttempts</c> ≤ 10 (Identity default is 5 —
///     safe; our old broken config had 50, which is effectively unlimited
///     brute-force)
///   - <c>Lockout.AllowedForNewUsers</c> = true (lockout must apply during
///     the initial login attempts, not just after the first success)
///   - <c>User.RequireUniqueEmail</c> = true (Identity default is false —
///     allows unlimited accounts with empty emails; our policy requires
///     unique emails so accounts are recoverable and auditable)
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
        // ASP.NET Identity default is MaxFailedAccessAttempts=5 (safe).
        // Our broken config had 50 (unsafe). We cap at 10 to catch a
        // re-introduction of the unsafe value — whether from a broken
        // binding OR an operator misconfiguration.
        if (options.Lockout.MaxFailedAccessAttempts > 10)
        {
            failures.Add(
                "TakOne:Identity:Lockout:MaxFailedAccessAttempts must be ≤ 10 " +
                $"(industry standard). Bound value: {options.Lockout.MaxFailedAccessAttempts}. " +
                "Higher values allow effectively unlimited brute-force attempts.");
        }

        if (!options.Lockout.AllowedForNewUsers)
        {
            failures.Add(
                "TakOne:Identity:Lockout:AllowedForNewUsers must be true — lockout " +
                "must apply during initial login attempts.");
        }

        // ── User policy ──
        // ASP.NET Identity default is RequireUniqueEmail=false (unsafe).
        // Our policy requires true — so a broken binding (defaults take
        // over) fails this check LOUDLY at startup.
        if (!options.User.RequireUniqueEmail)
        {
            failures.Add(
                "TakOne:Identity:User:RequireUniqueEmail must be true — without it, " +
                "unlimited accounts can be created with empty emails (no recovery " +
                "flow, no verification). If this is false, the config-binding path " +
                "is wrong — Identity options are NOT reaching the bound instance. " +
                "Check that appsettings.json places 'Identity' as a sibling of " +
                "'TakOne:Database' (NOT nested under it). " +
                "See Brutal Code Review v3 findings #01 + #22.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

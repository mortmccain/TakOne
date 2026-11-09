using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using TakOne.Infrastructure.Identity;
using Xunit;

namespace TakOne.Infrastructure.Tests.Identity;

/// <summary>
/// Unit tests for <see cref="TakOneIdentityOptionsValidator"/> — the
/// startup fail-fast gate for ASP.NET Identity configuration
/// (Brutal Code Review v3 finding #01's safety net).
///
/// The validator had ZERO direct tests until QA-18; it gained new
/// invariants when the owner consciously selected the elderly-workforce
/// lockout policy (MaxFailedAccessAttempts=50 in Production, paired
/// with always-reset-lockout-on-success). These tests pin BOTH sides of
/// that pairing plus the original fail-fast contract.
/// </summary>
/// <remarks>
/// COVERED INVARIANTS:
/// <list type="bullet">
/// <item><description>The owner-approved ceiling: values ≤ 50 pass, &gt; 50 fail
/// LOUDLY (a stray 500 or a re-introduced broken config can never
/// boot silently).</description></item>
/// <item><description>The .NET 10 reset-on-success AppContext switch
/// <c>Microsoft.AspNetCore.Identity.CheckPasswordSignInAlwaysResetLockoutOnSuccess</c>
/// — set by <c>AddTakOneInfrastructure</c>, enforced here so the
/// "50 attempts ⇒ reset-on-success" pairing cannot be silently
/// broken. In .NET 10 this is NOT a <c>LockoutOptions</c> property
/// (verified against the 10.0 reference source:
/// <c>SignInManager.CheckPasswordSignInCoreAsync</c> reads the
/// switch; without it, 2FA-enabled users accumulate failures across
/// days).</description></item>
/// <item><description>The original binding-bug contract: ASP.NET Identity DEFAULTS
/// (RequiredLength=6) FAIL the validator — a broken config-binding
/// path cannot boot silently.</description></item>
/// </list>
/// TEST-ISOLATION NOTE: <see cref="AppContext.SetSwitch(string, bool)"/>
/// is PROCESS-GLOBAL. All tests that flip the switch live in THIS class
/// and xunit runs tests within a single class sequentially, so each
/// test's explicit SetSwitch at its start is deterministic. No other
/// test class in this project resolves IdentityOptions through the
/// validator (nothing else constructs it), so the process-global flip
/// cannot leak into unrelated tests. The disabled-switch test restores
/// the switch to true in a finally block as defense-in-depth for any
/// future parallel class.
/// </remarks>
public class TakOneIdentityOptionsValidatorTests
{
    private const string ResetLockoutSwitchName =
        "Microsoft.AspNetCore.Identity.CheckPasswordSignInAlwaysResetLockoutOnSuccess";

    private readonly TakOneIdentityOptionsValidator _sut = new();

    /// <summary>
    /// Builds IdentityOptions that meet the FULL security policy — the
    /// QA-18 Production shape: RequiredLength=8, all character classes,
    /// MaxFailedAccessAttempts=50 (the owner-approved ceiling, used AT
    /// the boundary on purpose), AllowedForNewUsers=true.
    /// </summary>
    private static IdentityOptions PolicyCompliantOptions() => new()
    {
        Password =
        {
            RequiredLength = 8,
            RequireNonAlphanumeric = true,
            RequireUppercase = true,
            RequireLowercase = true,
            RequireDigit = true,
        },
        Lockout =
        {
            MaxFailedAccessAttempts = 50,
            AllowedForNewUsers = true,
        },
    };

    // ── Green paths ────────────────────────────────────────────────

    [Fact]
    public void Validate_PolicyCompliantOptions_AttemptCeilingInclusive_Succeeds()
    {
        // MaxFailedAccessAttempts=50 is the OWNER-APPROVED maximum —
        // the ceiling must be inclusive (50 passes; only >50 fails).
        AppContext.SetSwitch(ResetLockoutSwitchName, true);

        var result = _sut.Validate(name: null, PolicyCompliantOptions());

        result.Succeeded.Should().BeTrue(
            "the options meet the full policy: RequiredLength=8, all character classes, " +
            "MaxFailedAccessAttempts=50 (the owner-approved ceiling, inclusive), " +
            "AllowedForNewUsers=true, and the reset-on-success switch is enabled");
    }

    [Fact]
    public void Validate_PolicyCompliantOptions_DevThresholdFive_Succeeds()
    {
        // Base/Development configs keep MaxFailedAccessAttempts=5 —
        // anything ≤ 50 passes the ceiling.
        AppContext.SetSwitch(ResetLockoutSwitchName, true);

        var options = PolicyCompliantOptions();
        options.Lockout.MaxFailedAccessAttempts = 5;

        var result = _sut.Validate(name: null, options);

        result.Succeeded.Should().BeTrue(
            "the ceiling is ≤ 50; the tighter Development value of 5 passes");
    }

    // ── The 50 ceiling ─────────────────────────────────────────────

    [Fact]
    public void Validate_MaxFailedAccessAttempts_AboveOwnerCeiling_Fails()
    {
        // 51 = one past the owner-approved maximum. A stray 500, a
        // re-introduced broken config, or a fat-fingered override must
        // fail LOUDLY at startup — never boot silently.
        AppContext.SetSwitch(ResetLockoutSwitchName, true);

        var options = PolicyCompliantOptions();
        options.Lockout.MaxFailedAccessAttempts = 51;

        var result = _sut.Validate(name: null, options);

        result.Failed.Should().BeTrue(
            "51 exceeds the owner-approved maximum of 50");
        result.Failures.Should().Contain(s =>
                s.Contains("MaxFailedAccessAttempts must be ≤ 50") && s.Contains("51"),
            "the failure must name the invariant AND the bound value so the operator " +
            "can fix the config without reading source code");
    }

    // ── The reset-on-success switch invariant ──────────────────────

    [Fact]
    public void Validate_ResetLockoutSwitchDisabled_Fails()
    {
        // The switch is the structural mitigation that makes a
        // 50-attempt threshold safe. A host that wires Identity without
        // AddTakOneInfrastructure's SetSwitch must fail fast, not boot
        // with cross-day failure accumulation.
        AppContext.SetSwitch(ResetLockoutSwitchName, false);
        try
        {
            var result = _sut.Validate(name: null, PolicyCompliantOptions());

            result.Failed.Should().BeTrue(
                "the reset-on-success switch is the required companion of the " +
                "owner-approved 50-attempt policy");
            result.Failures.Should().Contain(s =>
                    s.Contains(ResetLockoutSwitchName),
                "the failure must name the exact AppContext switch so the operator " +
                "knows it is a code-wiring concern, not an appsettings key (there is " +
                "no LockoutOptions property for it in .NET 10)");
        }
        finally
        {
            // Defense-in-depth: leave the process in the app's state.
            AppContext.SetSwitch(ResetLockoutSwitchName, true);
        }
    }

    // ── The original fail-fast binding contract ────────────────────

    [Fact]
    public void Validate_IdentityDefaults_BrokenBindingPath_Fails()
    {
        // The Brutal Code Review v3 contract: if the config-binding path
        // is ever re-broken, ASP.NET Identity DEFAULTS take over
        // (RequiredLength=6) and the validator must refuse to boot.
        AppContext.SetSwitch(ResetLockoutSwitchName, true);

        var result = _sut.Validate(name: null, new IdentityOptions());

        result.Failed.Should().BeTrue(
            "ASP.NET Identity's raw defaults (RequiredLength=6) violate the OWASP " +
            "minimum of 8 — this is the loud failure that makes a broken binding " +
            "path impossible to miss");
        result.Failures.Should().Contain(s =>
                s.Contains("RequiredLength must be ≥ 8"),
            "the RequiredLength invariant is the regression-proof for the 3-cycles-missed-the-bug config-binding defect");
    }

    // ── Named-options guard ────────────────────────────────────────

    [Fact]
    public void Validate_NamedOptionsInstance_SkipsValidation()
    {
        // Named options are rare for Identity but supported by the
        // options system; the validator only polices the DEFAULT
        // (unnamed) instance to avoid false positives.
        AppContext.SetSwitch(ResetLockoutSwitchName, true);

        var options = PolicyCompliantOptions();
        options.Password.RequiredLength = 6; // would FAIL if validated

        var result = _sut.Validate(name: "SomeNamedInstance", options);

        result.Skipped.Should().BeTrue(
            "named IdentityOptions instances are deliberately not validated — " +
            "only the default (unnamed) instance is subject to the policy");
    }
}

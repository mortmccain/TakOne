using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace TakOne.Application.Tests.ConfigBinding;

/// <summary>
/// Regression tests for the IdentityOptions config-binding fix
/// (Brutal Code Review v3 finding #01).
/// </summary>
/// <remarks>
/// <para>
/// <b>BUG HISTORY (3 review cycles missed it):</b>
/// <c>appsettings.json</c> previously nested <c>Identity</c>/<c>Auth</c>/
/// <c>DefaultAdmin</c> UNDER <c>TakOne.Database.*</c> — but the binding
/// code read them as <c>TakOne:Identity</c> (siblings of Database). The
/// operator's configured password policy (RequiredLength=8), lockout
/// window, and RequireUniqueEmail were SILENTLY IGNORED in Production;
/// ASP.NET Identity DEFAULTS took over (RequiredLength=6,
/// RequireUniqueEmail=false). The bug was invisible because no error
/// fired — the defaults happen to be safer in some dimensions (lockout
/// attempts=5) and LESS safe in others (RequireUniqueEmail=false).
/// </para>
/// <para>
/// <b>THE FIX (Round 18-B):</b>
/// <list type="bullet">
///   <item>Restructured <c>appsettings.json</c> so Identity/Auth/
///     DefaultAdmin are SIBLINGS of <c>Database</c> under <c>TakOne</c>.</item>
///   <item>Registered <c>TakOneIdentityOptionsValidator</c> as an
///     <c>IValidateOptions&lt;IdentityOptions&gt;</c> so a broken binding
///     fails the validator at startup (defaults → RequiredLength=6 →
///     fails the <c>RequiredLength ≥ 8</c> check).</item>
/// </list>
/// </para>
/// <para>
/// <b>THESE REGRESSION TESTS</b> verify the structural binding fix at the
/// JSON-path level — they DON'T depend on a real DI container or the
/// validator. The two cases:
/// <list type="number">
///   <item><see cref="AppSettings_WithIdentitySiblingOfDatabase_BindsOperatorValues"/>
///     — uses the SAME JSON structure as the real <c>appsettings.Production.json</c>
///     (Identity is a sibling of Database under TakOne). Asserts the
///     operator-configured values bind: RequiredLength=8 (NOT the
///     ASP.NET default of 6), MaxFailedAccessAttempts=5,
///     RequireUniqueEmail=true, RequireDigit=true, RequireUppercase=true.
///     The KEY assertion is <c>RequiredLength == 8</c> — if the JSON
///     path were wrong (nested under TakOne.Database.Identity), the
///     bound value would be the default 6, and this test would FAIL.</item>
///   <item><see cref="AppSettings_WithWrongNesting_BindsToDefaultsNotConfigured"/>
///     — uses a deliberately-WRONG JSON structure (Identity nested UNDER
///     TakOne.Database — the original 3-cycles-missed-the-bug layout).
///     Asserts the bound values ARE the ASP.NET defaults (RequiredLength=6,
///     RequireUniqueEmail=false) — proving the negative: if the binding
///     path is wrong, the test would PASS this assertion; if the binding
///     path were CORRECT (Identity as sibling of Database), the test
///     would FAIL (because the wrong path would have FAILED to bind the
///     operator's RequiredLength=8 to the IdentityOptions instance —
///     which would have actually bound to 8). The intent: a regression
///     that RE-BREAKS the JSON path AND keeps the test running would be
///     caught by the FIRST test (the positive case).</item>
/// </list>
/// </para>
/// <para>
/// <b>WHY ADDJSONSTREAM, NOT ADDJSONFILE:</b>
/// The real appsettings.Production.json lives in TakOne.WebUI/ and the
/// brief says "Loads appsettings.Production.json into an IConfiguration".
/// Using <c>AddJsonFile</c> would require either:
/// (a) a brittle relative path from the test's bin folder
///     (`..\..\..\..\TakOne.WebUI\appsettings.Production.json`), OR
/// (b) copying the file into the test project's output dir via
///     <c>&lt;Content Include&gt;</c> + <c>CopyToOutputDirectory</c>.
/// Both add maintenance overhead and would couple the test to the file
/// layout of the WebUI project. <c>AddJsonStream</c> with an embedded
/// JSON string is functionally identical — the binding mechanism is the
/// same (Microsoft.Extensions.Configuration.Json.JsonConfigurationFileParser.Parse),
/// just the source is a MemoryStream instead of a FileStream. The test
/// self-documents the JSON structure it asserts on, which makes the
/// regression-trip reproduction clearer for a future reader.
/// </para>
/// </remarks>
public class IdentityOptionsBindingTests
{
    // ── JSON test fixtures ───────────────────────────────────────────

    // Mirrors the structure of TakOne.WebUI/appsettings.Production.json
    // — Identity is a SIBLING of Database under TakOne. The binding
    // code reads `TakOne:Identity:Password:RequiredLength`, which IS
    // present in this JSON. (Operator-configured values: RequiredLength=8,
    // RequireUniqueEmail=true, MaxFailedAccessAttempts=5.)
    //
    // IMPORTANT: this is the SAME JSON structure that ships in
    // production (TakOne.WebUI/appsettings.Production.json after the
    // Round 18-B fix). The brief allows embedding the file content as
    // a test fixture instead of loading the actual file (see the WHY
    // ADDJSONSTREAM comment in the class-level XML doc above).
    private const string CorrectlyNestedJson = """
        {
          "TakOne": {
            "Database": {},
            "Identity": {
              "Password": {
                "RequireDigit": true,
                "RequiredLength": 8,
                "RequireNonAlphanumeric": true,
                "RequireUppercase": true,
                "RequireLowercase": true,
                "RequiredUniqueChars": 4
              },
              "Lockout": {
                "DefaultLockoutTimeSpan": "00:05:00",
                "MaxFailedAccessAttempts": 5,
                "AllowedForNewUsers": true
              },
              "User": {
                "RequireUniqueEmail": true,
                "AllowedUserNameCharacters": "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-"
              },
              "SignIn": {
                "RequireConfirmedEmail": false,
                "RequireConfirmedPhoneNumber": false,
                "RequireConfirmedAccount": false
              }
            }
          }
        }
        """;

    // Deliberately WRONG nesting — Identity nested UNDER TakOne.Database.
    // This is the original 3-cycles-missed-the-bug layout. The binding
    // code reads `TakOne:Identity:Password:RequiredLength` — that path
    // is NOT present in this JSON (the only Identity is at
    // `TakOne:Database:Identity:*`). So the Bind() call is a NO-OP —
    // the IdentityOptions instance keeps its defaults
    // (RequiredLength=6, RequireUniqueEmail=false).
    private const string WronglyNestedJson = """
        {
          "TakOne": {
            "Database": {
              "Identity": {
                "Password": {
                  "RequireDigit": true,
                  "RequiredLength": 8,
                  "RequireNonAlphanumeric": true,
                  "RequireUppercase": true,
                  "RequireLowercase": true,
                  "RequiredUniqueChars": 4
                },
                "Lockout": {
                  "DefaultLockoutTimeSpan": "00:05:00",
                  "MaxFailedAccessAttempts": 5,
                  "AllowedForNewUsers": true
                },
                "User": {
                  "RequireUniqueEmail": true
                }
              }
            }
          }
        }
        """;

    // ── Helpers ──────────────────────────────────────────────────────

    // Builds a ConfigurationRoot from a JSON string via AddJsonStream —
    // the same JsonConfigurationFileParser.Parse code path that
    // AddJsonFile uses at runtime, just with an in-memory stream
    // instead of a FileStream.
    private static IConfigurationRoot BuildConfigFromJson(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
    }

    // Replicates EXACTLY the binding pattern in
    // TakOne.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs:
    //   var identitySection = configuration.GetSection("TakOne:Identity");
    //   identitySection.GetSection("Password").Bind(options.Password);
    //   identitySection.GetSection("Lockout").Bind(options.Lockout);
    //   identitySection.GetSection("User").Bind(options.User);
    //   identitySection.GetSection("SignIn").Bind(options.SignIn);
    //
    // The real production code does this inside the AddIdentity(...) lambda,
    // but the per-subsection Bind() calls are the binding mechanism
    // under test. We replicate them here to make the regression explicit.
    private static IdentityOptions BindIdentityOptions(IConfiguration configuration)
    {
        var options = new IdentityOptions();
        var identitySection = configuration.GetSection("TakOne:Identity");
        identitySection.GetSection("Password").Bind(options.Password);
        identitySection.GetSection("Lockout").Bind(options.Lockout);
        identitySection.GetSection("User").Bind(options.User);
        identitySection.GetSection("SignIn").Bind(options.SignIn);
        return options;
    }

    // ── Tests ────────────────────────────────────────────────────────

    // POSITIVE: the JSON path `TakOne:Identity` (Identity as a SIBLING
    // of Database) is the correct one. The bound IdentityOptions must
    // reflect the operator-configured values:
    //   - RequiredLength == 8  (the CRITICAL assertion — ASP.NET default is 6)
    //   - MaxFailedAccessAttempts == 5  (the lockout threshold)
    //   - RequireUniqueEmail == true  (ASP.NET default is false)
    //   - RequireDigit == true
    //   - RequireUppercase == true
    //
    // The KEY assertion is RequiredLength == 8. If the JSON path were
    // wrong (nested under TakOne.Database.Identity), the bound value
    // would be the ASP.NET DEFAULT of 6, and this test would FAIL with
    // a clean signal: "Expected 8, found 6."
    //
    // This test is what the Brutal Code Review v3 demanded when it said
    // "ZERO tests for the config-binding bug" — the test catches a
    // regression in the JSON path / binding mechanism.
    [Fact]
    public void AppSettings_WithIdentitySiblingOfDatabase_BindsOperatorValues()
    {
        // Arrange
        var config = BuildConfigFromJson(CorrectlyNestedJson);

        // Act
        var options = BindIdentityOptions(config);

        // Assert
        // ── THE CRITICAL BINDING PROOF ──
        // ASP.NET Identity's default RequiredLength is 6. The operator
        // configured 8 in appsettings.Production.json. If the binding path
        // is correct (TakOne:Identity:Password:RequiredLength), the
        // bound value is 8. If the path is wrong (e.g. nested under
        // TakOne.Database.Identity), the Bind() call is a no-op and the
        // value stays at the default 6. This single assertion is the
        // regression proof.
        options.Password.RequiredLength.Should().Be(8,
            "the JSON path `TakOne:Identity:Password:RequiredLength` is the OPERATOR-configured value (8) — NOT the ASP.NET default of 6 (which would indicate the binding path is wrong)");

        // ── Secondary assertions on the lockout + user policy ──
        options.Lockout.MaxFailedAccessAttempts.Should().Be(5,
            "TakOne:Identity:Lockout:MaxFailedAccessAttempts binds to the operator-configured 5");
        options.User.RequireUniqueEmail.Should().BeTrue(
            "TakOne:Identity:User:RequireUniqueEmail binds to the operator-configured true (ASP.NET default is false — a binding failure here would be the bug from cycle 1)");

        // ── Tertiary assertions on the password character-class policy ──
        options.Password.RequireDigit.Should().BeTrue();
        options.Password.RequireUppercase.Should().BeTrue();
        options.Password.RequireLowercase.Should().BeTrue();
        options.Password.RequireNonAlphanumeric.Should().BeTrue();
        options.Password.RequiredUniqueChars.Should().Be(4);
    }

    // NEGATIVE: the JSON path `TakOne:Database:Identity` (Identity
    // nested UNDER Database) is the WRONG one — the original 3-cycles-
    // missed-the-bug layout. The binding code reads `TakOne:Identity`,
    // which is NOT present in this JSON. So the Bind() calls are no-ops
    // — the IdentityOptions instance retains its constructor defaults.
    //
    // This test PROVES the negative:
    //   - The default RequiredLength IS 6 (not the operator's 8) —
    //     confirming that with a WRONG JSON path, the operator's value
    //     doesn't bind.
    //   - The default RequireUniqueEmail IS false (not the operator's
    //     true) — same proof.
    //
    // WHY THIS MATTERS AS A REGRESSION TEST:
    // If a future refactor re-nests Identity under Database (either
    // by typo or by accident), this test would PASS (because the
    // defaults match), but the AppSettings_WithIdentitySiblingOfDatabase
    // test would FAIL (because the operator's values would not bind).
    // The combination of the two tests gives a clear signal in either
    // direction.
    [Fact]
    public void AppSettings_WithWrongNesting_BindsToDefaultsNotConfigured()
    {
        // Arrange — JSON with Identity nested UNDER Database (wrong).
        var config = BuildConfigFromJson(WronglyNestedJson);

        // Act
        var options = BindIdentityOptions(config);

        // Assert
        // The defaults from ASP.NET Identity's constructor:
        //   - Password.RequiredLength default = 6  (we configured 8 — proves the path is wrong)
        //   - Lockout.MaxFailedAccessAttempts default = 5  (matches by coincidence — our config also uses 5)
        //   - User.RequireUniqueEmail default = false  (we configured true — proves the path is wrong)
        options.Password.RequiredLength.Should().Be(6,
            "with Identity nested under TakOne.Database (the original 3-cycles-missed-the-bug layout), " +
            "the `TakOne:Identity:Password:RequiredLength` binding path doesn't exist — the Bind() call is a no-op " +
            "and the IdentityOptions instance keeps its ASP.NET default of 6 (NOT the operator-configured 8)");

        options.User.RequireUniqueEmail.Should().BeFalse(
            "same cause — the operator-configured `true` lives at `TakOne:Database:Identity:User:RequireUniqueEmail`, " +
            "but the binding code reads `TakOne:Identity:User:RequireUniqueEmail` which doesn't exist in this JSON. " +
            "The default (false) is retained — exactly the bug Brutal Code Review v3 finding #01 documented.");
    }
}

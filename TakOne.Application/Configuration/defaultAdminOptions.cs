using Microsoft.Extensions.Hosting;

namespace TakOne.Application.Configuration;

/// <summary>
/// Strongly-typed options for the bootstrap (default) administrator account
/// that <c>TakOne.Infrastructure.Identity.DefaultAdminSeeder</c> creates on
/// first startup.
///
/// Bound from the <c>"TakOne:DefaultAdmin"</c> section of the application
/// configuration (appsettings.json, appsettings.&lt;Env&gt;.json, environment
/// variables, Azure Key Vault, .NET user secrets — whatever
/// <c>IConfiguration</c> is composed from).
///
/// WHY THIS EXISTS (security audit — Issue #02):
///   The previous implementation hard-coded the default admin's WorkerId,
///   Email, AND Password as <c>public const string</c> fields on the seeder
///   class. Anyone with read access to the source repository (or a built
///   binary, via string-dumping) knew the default admin password for every
///   fresh install. Worse, the seeder logged the password at WARNING level
///   on first creation, leaking it to anyone with read access to production
///   logs.
///
///   This options class fixes the source-side leak: the password is no
///   longer in source. It is supplied through the configuration pipeline,
///   which in Development pulls from .NET user secrets
///   (<c>%APPDATA%\Microsoft\UserSecrets\&lt;UserSecretsId&gt;\secrets.json</c>)
///   and in Production pulls from an environment variable
///   (<c>TakOne__DefaultAdmin__Password</c>) or a secret store such as
///   Azure Key Vault / HashiCorp Vault / AWS Secrets Manager.
///
/// WHY A STRONGLY-TYPED OPTIONS CLASS (not raw IConfiguration reads):
///   - Named keys: <c>options.Password</c> is greppable and refactor-safe;
///     <c>configuration["TakOne:DefaultAdmin:Password"]</c> is a magic string.
///   - Validation: <see cref="EnsureValid"/> is called at startup so a
///     missing password in a non-Development environment fails FAST with a
///     clear message instead of seeding an admin with an empty password.
///   - Testability: tests can construct a <see cref="DefaultAdminOptions"/>
///     directly and pass it to the seeder without needing a fake
///     <c>IConfiguration</c>.
///
/// WHAT GOES WHERE:
///   <list type="table">
///     <item>
///       <term><c>WorkerId</c>, <c>Email</c>, <c>ForcePasswordChangeOnFirstLogin</c>, <c>Enabled</c></term>
///       <description>
///         Non-secret defaults. Safe to commit in <c>appsettings.json</c>.
///         <c>WorkerId</c> / <c>Email</c> are not secrets — they're just
///         well-known bootstrap identifiers. <c>Enabled</c> defaults to
///         <c>true</c> in Development and <c>false</c> in Production
///         (see <see cref="EnsureValid"/>).
///       </description>
///     </item>
///     <item>
///       <term><c>Password</c></term>
///       <description>
///         SECRET. NEVER commit to source. In Development, store it in .NET
///         user secrets:
///         <code>
///         dotnet user-secrets set "TakOne:DefaultAdmin:Password" "&lt;your-strong-password&gt;" --project TakOne.WebUI
///         </code>
///         In Production, set the environment variable
///         <c>TakOne__DefaultAdmin__Password</c> (double-underscore is the
///         <c>:</c> separator on non-Windows / in Docker / in Kubernetes)
///         or mount it from Azure Key Vault / a secrets manager.
///       </description>
///     </item>
///   </list>
///
/// SECURITY GUARANTEES ENFORCED BY THIS CLASS:
///   <item>
///     The password is REQUIRED in non-Development environments. A missing
///     password in Production throws <see cref="InvalidOperationException"/>
///     at startup — the application refuses to start rather than seeding an
///     admin with an empty (or default) password.
///   </item>
///   <item>
///     The seeder is DISABLED by default in non-Development environments
///     (unless <c>Enabled</c> is explicitly set to <c>true</c>). This means
///     a Production deployment will not silently create a bootstrap admin
///     on first startup — the operator must opt in.
///   </item>
///   <item>
///     The password value is never logged. <see cref="EnsureValid"/> does
///     not log, the seeder does not log it, and no other code path logs it.
///     See the audit note on the seeder for the full rationale.
///   </item>
/// </summary>
public sealed class DefaultAdminOptions
{
    /// <summary>
    /// The configuration section key these options are bound from.
    /// Centralized here so the binding call site and any documentation
    /// reference the same constant.
    /// </summary>
    public const string SectionName = "TakOne:DefaultAdmin";

    /// <summary>
    /// The default admin's login identifier (used as <c>UserName</c> on
    /// <c>ApplicationUser</c> and <c>WorkerId</c> on the Domain <c>User</c>).
    ///
    /// Defaults to <c>ADMIN-0001</c>. Not a secret — it's a well-known
    /// bootstrap identifier that is documented in the README and in the
    /// seeder's XML doc. Override via <c>TakOne:DefaultAdmin:WorkerId</c>
    /// if you want a different bootstrap login (e.g. <c>root</c> or a
    /// customer-specific convention).
    /// </summary>
    public string WorkerId { get; set; } = "ADMIN-0001";

    /// <summary>
    /// The default admin's email. Uses the <c>.local</c> TLD by default so
    /// it can never accidentally route to a real mailbox. Not a secret.
    /// </summary>
    public string Email { get; set; } = "admin@takone.local";

    /// <summary>
    /// The default admin's initial password. SECRET — must be supplied via
    /// user secrets (Development) or environment variable / secret store
    /// (Production). NEVER commit a value for this property in
    /// <c>appsettings.json</c> or any other source-controlled file.
    ///
    /// The password must meet Identity's complexity rules (configured under
    /// <c>TakOne:Identity:Password</c> in <c>appsettings.json</c>). The
    /// default rules are: 8+ chars, upper + lower + digit + non-alphanumeric,
    /// 4+ unique chars.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Whether the seeder should run at all. Defaults to <c>false</c> —
    /// the seeder only runs if either:
    ///   <list type="bullet">
    ///     <item>The host environment is Development, OR</item>
    ///     <item>This property is explicitly set to <c>true</c> in
    ///           configuration (e.g. for a fresh Production deployment that
    ///           needs the bootstrap admin created automatically).</item>
    ///   </list>
    /// This avoids the previous behavior of unconditionally attempting to
    /// seed on every startup in every environment.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Whether the seeded admin must change their password on first login.
    ///
    /// Defaults to <c>true</c>. This is a defense-in-depth measure: even
    /// though the password is supplied via configuration (not source), the
    /// operator who set it may have shared it with the eventual human admin
    /// out-of-band (Slack, phone call, etc.). Forcing a one-time password
    /// change on first login ensures that the password known to the
    /// operator and the password known to the human admin are different
    /// from that point forward — the operator cannot log in as the admin
    /// after first login.
    ///
    /// Setting this to <c>false</c> is allowed (for fully-automated
    /// deployments where the configured password is already a unique,
    /// non-shared secret) but NOT recommended.
    /// </summary>
    public bool ForcePasswordChangeOnFirstLogin { get; set; } = true;

    /// <summary>
    /// Validates that the options are usable for the given host environment.
    /// Called at startup by <c>Program.cs</c> BEFORE the seeder runs.
    ///
    /// RULES:
    /// <list type="bullet">
    ///   <item>
    ///     In a non-Development environment where <c>Enabled</c> is
    ///     <c>true</c>: the password MUST be supplied. A missing password
    ///     throws <see cref="InvalidOperationException"/> — the application
    ///     refuses to start rather than seed an admin with an empty
    ///     password.
    ///   </item>
    ///   <item>
    ///     In a non-Development environment where <c>Enabled</c> is
    ///     <c>false</c>: the seeder is disabled, so we don't validate the
    ///     password. The operator may have intentionally left it empty.
    ///   </item>
    ///   <item>
    ///     In Development: if the password is missing, we don't throw —
    ///     but we don't run the seeder either (the seeder skips itself if
    ///     the password is empty). This lets a developer clone the repo and
    ///     <c>dotnet run</c> without immediately configuring user secrets;
    ///     they just won't get a default admin until they do.
    ///   </item>
    /// </list>
    ///
    /// We throw rather than return a bool because a missing Production
    /// password is a deployment misconfiguration — there's no recovery path
    /// other than fixing the configuration and restarting.
    ///
    /// SECURITY: this method does NOT log the password value. It only
    /// references the configuration KEY (<c>TakOne:DefaultAdmin:Password</c>)
    /// in its exception message, never the value.
    /// </summary>
    /// <param name="hostEnvironment">
    /// The current <c>IHostEnvironment</c> (passed in by Program.cs).
    /// Used to distinguish Development from Production-style environments.
    /// </param>
    public void EnsureValid(IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        // If the seeder is disabled, no validation needed — the seeder
        // will short-circuit before touching any of these values.
        if (!Enabled && !hostEnvironment.IsDevelopment())
        {
            return;
        }

        // From here on, the seeder will run (either because Enabled=true
        // or because we're in Development where the seeder runs by default).
        // The WorkerId and Email have safe defaults, so we only need to
        // validate the password.

        if (string.IsNullOrWhiteSpace(Password))
        {
            // Different message for Development vs Production so the
            // operator gets the right remediation hint.
            if (hostEnvironment.IsDevelopment())
            {
                // In Development we don't throw — the seeder will skip
                // itself. We just log nothing here; the seeder logs an
                // informational message when it skips.
                return;
            }

            throw new InvalidOperationException(
                "The default administrator password is not configured, but the " +
                "default admin seeder is enabled in a non-Development environment. " +
                $"Set the password under '{SectionName}:Password' in a secret store " +
                $"or via the environment variable " +
                $"'{SectionName.Replace(":", "__")}__Password' " +
                $"(i.e. 'TakOne__DefaultAdmin__Password'). The application cannot " +
                $"start without it. NEVER commit the password to source control " +
                $"or to appsettings.json.");
        }

        if (Password.Length < 8)
        {
            // Defense-in-depth: even if Identity's password complexity
            // rules would catch this at seed time, fail fast at startup
            // with a clearer message.
            throw new InvalidOperationException(
                $"The default administrator password configured under " +
                $"'{SectionName}:Password' is shorter than 8 characters and " +
                $"will not pass Identity's default complexity rules. Set a " +
                $"stronger password before starting the application.");
        }
    }
}
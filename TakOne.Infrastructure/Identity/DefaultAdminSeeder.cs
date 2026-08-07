using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Configuration;
using TakOne.Domain.Users;
using TakOne.Infrastructure.Persistence;

namespace TakOne.Infrastructure.Identity;

/// <summary>
/// One-shot startup helper that creates a DEFAULT ADMIN USER if (and only
/// if) no user currently holds the <c>Admin</c> role.
///
/// WHY THIS EXISTS:
///   ASP.NET Identity ships with zero users. The RoleSeeder creates the 5
///   roles, but you still can't log in because there is no Admin to create
///   the first set of users through the UI (the user-management page is
///   locked to Admin/Manager). This seeder breaks the chicken-and-egg:
///   after the first <c>dotnet run</c>, you have exactly one admin you can
///   log in with — but ONLY in Development, or in Production when
///   explicitly opted in via <c>TakOne:DefaultAdmin:Enabled = true</c>.
///
/// WHEN IT RUNS:
///   From <c>Program.cs</c>, AFTER <see cref="RoleSeeder.EnsureRolesCreatedAsync"/>
///   (so the Admin role already exists when we try to assign it) and BEFORE
///   <c>app.RunAsync()</c>. Program.cs also gates the call:
///   <code>
///   if (builder.Environment.IsDevelopment() || adminOptions.Enabled)
///   {
///       await DefaultAdminSeeder.EnsureDefaultAdminAsync(app.Services, adminOptions);
///   }
///   </code>
///   so the seeder NEVER runs silently in a Production environment where
///   the operator has not opted in.
///
/// IDEMPOTENCY:
///   The seeder queries <c>UserManager.GetUsersInRoleAsync("Admin")</c>.
///   If the returned list is non-empty, the seeder is a no-op. Safe to call
///   on every startup. After the first run, subsequent runs do nothing.
///
/// SECURITY POSTURE (Issue #02 — Hardcoded default administrator password):
///   PREVIOUSLY: the default admin's WorkerId, Email, AND Password were
///   hard-coded as <c>public const string</c> fields on this class. The
///   seeder ran unconditionally on every startup, in every environment.
///   On first creation, it logged the password at WARNING level — leaking
///   it to anyone with read access to the source or the production logs.
///
///   NOW:
///     <item>
///       Credentials come from <see cref="DefaultAdminOptions"/>, which is
///       bound from the <c>TakOne:DefaultAdmin</c> configuration section.
///       The password is supplied via .NET user secrets (Development) or
///       an environment variable / secret store (Production). It is NEVER
///       in source control.
///     </item>
///     <item>
///       The seeder is gated by Program.cs — it only runs in Development,
///       or when <c>TakOne:DefaultAdmin:Enabled</c> is explicitly set to
///       <c>true</c> in a non-Development environment.
///     </item>
///     <item>
///       The seeder NEVER logs the password — not at WARNING, not at DEBUG,
///       not anywhere. The structured log on first creation reports only
///       the WorkerId and Email (which are not secrets — they're well-known
///       bootstrap identifiers). See the SECURITY NOTE on the
///       <c>LogInformation</c> call below for the rationale.
///     </item>
///     <item>
///       The seeded admin's <see cref="ApplicationUser.MustChangePassword"/>
///       flag is set to <c>true</c> (unless <c>ForcePasswordChangeOnFirstLogin</c>
///       is explicitly <c>false</c> in configuration). This forces the
///       first human admin to set their own password before accessing any
///       other page — so the password the operator configured (and may
///       have shared out-of-band) is no longer valid after first login.
///     </item>
/// </summary>
public static class DefaultAdminSeeder
{
    /// <summary>
    /// Creates the default admin user if no Admin role users exist.
    /// </summary>
    /// <param name="rootProvider">
    /// The root <c>IServiceProvider</c> from <see cref="WebApplication.Services"/>.
    /// </param>
    /// <param name="options">
    /// The bound <see cref="DefaultAdminOptions"/>. Caller is responsible
    /// for calling <see cref="DefaultAdminOptions.EnsureValid"/> BEFORE
    /// invoking this method — we don't re-validate here.
    /// </param>
    /// <param name="hostEnvironment">
    /// The current <c>IHostEnvironment</c>. Used only to log a more
    /// helpful message when the seeder skips itself in Development
    /// because the password is missing.
    /// </param>
    public static async Task EnsureDefaultAdminAsync(
        IServiceProvider rootProvider,
        DefaultAdminOptions options,
        IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        // ----------------------------------------------------------------
        // 0. If the password is empty, skip the seeder entirely.
        //
        //    In Development, this lets a developer `dotnet run` the app
        //    immediately after cloning — they just won't get a default
        //    admin until they set up user secrets. In Production, this
        //    branch is unreachable for an Enabled seeder because
        //    DefaultAdminOptions.EnsureValid throws before we get here.
        //    But defense-in-depth — never trust the caller.
        // ----------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(options.Password))
        {
            // We log at Information (not Warning) — this is an expected,
            // benign state in Development. The developer hasn't set up
            // user secrets yet.
            //
            // We log the configuration KEY (never the value — there is no
            // value to log here anyway, but the rule applies generally).
            using var scope0 = rootProvider.CreateScope();
            var skipLogger = scope0.ServiceProvider
                .GetService<ILoggerFactory>()
                ?.CreateLogger("DefaultAdminSeeder");
            skipLogger?.LogInformation(
                "DefaultAdminSeeder: skipping — no password configured under " +
                "'{SectionKey}:Password'. Set it via 'dotnet user-secrets set " +
                "\"{SectionKey}:Password\" \"&lt;your-strong-password&gt;\" " +
                "--project TakOne.WebUI' (Development) or the environment variable " +
                "'{EnvVarName}' (Production).",
                DefaultAdminOptions.SectionName,
                DefaultAdminOptions.SectionName,
                DefaultAdminOptions.SectionName.Replace(":", "__") + "__Password");
            return;
        }

        using var scope = rootProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var userAccountService = sp.GetRequiredService<IUserAccountService>();
        var loggerFactory = sp.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("DefaultAdminSeeder");

        // ----------------------------------------------------------------
        // 1. Verify the Admin role exists. RoleSeeder should have created
        //    it already, but defense-in-depth — if someone reordered the
        //    startup calls, this prevents a confusing "role not found"
        //    error from UserManager.AddToRoleAsync.
        // ----------------------------------------------------------------
        if (!await roleManager.RoleExistsAsync(Roles.Admin))
        {
            logger?.LogWarning(
                "DefaultAdminSeeder: Admin role does not exist yet. " +
                "RoleSeeder must run BEFORE DefaultAdminSeeder. Skipping " +
                "admin seeding for this startup; will retry next time.");
            return;
        }

        // ----------------------------------------------------------------
        // 2. Check if any user already holds the Admin role. If yes, the
        //    seeder is a no-op. GetUsersInRoleAsync normalizes the role
        //    name internally, so "admin" and "Admin" match.
        // ----------------------------------------------------------------
        var existingAdmins = await userManager.GetUsersInRoleAsync(Roles.Admin);
        if (existingAdmins.Count > 0)
        {
            logger?.LogDebug(
                "DefaultAdminSeeder: {Count} admin user(s) already exist. Skipping seeding.",
                existingAdmins.Count);
            return;
        }

        // ----------------------------------------------------------------
        // 3. Safety check: even though GetUsersInRoleAsync was empty, make
        //    sure nobody has already grabbed the configured WorkerId/Email —
        //    otherwise we'd collide on the unique index. This handles the
        //    "user was created but role-assignment failed" edge case.
        // ----------------------------------------------------------------
        var existingByWorkerId = await userManager.FindByNameAsync(options.WorkerId);
        if (existingByWorkerId is not null)
        {
            logger?.LogWarning(
                "DefaultAdminSeeder: a user with WorkerId '{WorkerId}' already " +
                "exists but is NOT in the Admin role. Skipping default admin " +
                "seeding to avoid duplicate. If this is unexpected, inspect " +
                "the AspNetUsers table manually.",
                options.WorkerId);
            return;
        }

        // ----------------------------------------------------------------
        // 4. Create the Domain User first. User.CreateStaff generates a new
        //    Guid; we'll use that same Guid when creating the ApplicationUser
        //    so the two share a primary key (the invariant documented on
        //    ApplicationUser).
        // ----------------------------------------------------------------
        var domainUser = User.CreateStaff(
            workerId: options.WorkerId,
            fullName: "System Administrator",
            gender: Gender.Male);

        await db.DomainUsers.AddAsync(domainUser);
        await db.SaveChangesAsync();

        // ----------------------------------------------------------------
        // 5. Create the Identity account (ApplicationUser + password + role
        //    + email-confirmed). IUserAccountService.CreateIdentityAccountAsync
        //    does all four atomically.
        // ----------------------------------------------------------------
        var result = await userAccountService.CreateIdentityAccountAsync(
            userId: domainUser.Id,
            workerId: options.WorkerId,
            email: options.Email,
            initialPassword: options.Password,
            role: Roles.Admin,
            gender: Gender.Male);

        if (result.IsSuccess)
        {
            // ------------------------------------------------------------
            // 6. Mark email as confirmed so the user can sign in without
            //    an email loop. IUserAccountService.CreateIdentityAccountAsync
            //    already does this in its documented contract, but we
            //    re-affirm it here in case the implementation changes —
            //    without a confirmed email, Identity's default sign-in
            //    flow may refuse to issue a cookie if RequireConfirmedEmail
            //    is ever flipped to true in appsettings.json.
            // ------------------------------------------------------------
            var appUser = await userManager.FindByIdAsync(domainUser.Id.ToString());
            if (appUser is not null)
            {
                if (!appUser.EmailConfirmed)
                {
                    var token = await userManager.GenerateEmailConfirmationTokenAsync(appUser);
                    await userManager.ConfirmEmailAsync(appUser, token);
                }

                // --------------------------------------------------------
                // 7. Honor the ForcePasswordChangeOnFirstLogin opt-out.
                //
                //    UserAccountService.CreateIdentityAccountAsync now sets
                //    MustChangePassword = true by DEFAULT on every new
                //    ApplicationUser (Issue #08 — all new users must change
                //    their password on first login). The bootstrap admin is
                //    created via that same method, so by the time we reach
                //    here, appUser.MustChangePassword is already true.
                //
                //    If the operator explicitly opted out
                //    (ForcePasswordChangeOnFirstLogin = false), we override
                //    the default back to false here. This is the ONLY way to
                //    get a bootstrap admin that is NOT forced to change
                //    their password — recommended only for fully-automated
                //    deployments where the configured password is already a
                //    unique, non-shared secret.
                //
                //    If ForcePasswordChangeOnFirstLogin = true (the default),
                //    MustChangePassword is already true from step 5 — no
                //    action needed (setting true to true is a no-op we just
                //    skip).
                // --------------------------------------------------------
                if (!options.ForcePasswordChangeOnFirstLogin)
                {
                    appUser.MustChangePassword = false;
                    await userManager.UpdateAsync(appUser);
                }
            }

            // ------------------------------------------------------------
            // SECURITY NOTE on logging (Issue #02 — "Never log passwords"):
            //
            // The previous implementation logged the password at WARNING
            // level on first creation, supposedly so the developer would
            // notice it. That was a leak — anyone with read access to
            // production logs (operators, log aggregators, SIEM systems,
            // backups, etc.) would know the default admin password for
            // every fresh install.
            //
            // We now log ONLY the WorkerId and Email. These are not
            // secrets — they're well-known bootstrap identifiers
            // (defaulting to "ADMIN-0001" and "admin@takone.local") that
            // are documented in the README and in this file. The password
            // is supplied by the operator via configuration; if they've
            // forgotten it, they can re-read their secret store.
            //
            // We log at Information (not Warning) — a successful seed is
            // an expected, normal event, not something that warrants
            // attention. Warning would just train operators to ignore
            // warnings.
            //
            // We NEVER log the password — not at Information, not at
            // Debug, not at Trace, not anywhere. Even at Debug/Trace,
            // logs get captured by log aggregators and persisted to
            // long-term storage; "debug-only" is a false comfort.
            // ------------------------------------------------------------
            logger?.LogInformation(
                "DefaultAdminSeeder: CREATED default admin user. " +
                "WorkerId (login): {WorkerId}, Email: {Email}. " +
                "The password is not logged — see the configuration source " +
                "(user secrets / environment variable / Key Vault) if you " +
                "need to retrieve it. MustChangePassword={MustChange} " +
                "(forced change is the default; operator opt-out is honored by overriding the flag back to false).",
                options.WorkerId,
                options.Email,
                options.ForcePasswordChangeOnFirstLogin);
        }
        else
        {
            // If Identity account creation failed, roll back the Domain User
            // we already saved so a retry on the next startup doesn't trip
            // the unique index on WorkerId.
            db.DomainUsers.Remove(domainUser);
            await db.SaveChangesAsync();

            logger?.LogError(
                "DefaultAdminSeeder: FAILED to create default admin. " +
                "Rolled back the Domain User. Errors: {Errors}.",
                result.Error ?? "unknown");
        }
    }
}
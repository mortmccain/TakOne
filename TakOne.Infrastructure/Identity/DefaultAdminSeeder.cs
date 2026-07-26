using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
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
///   log in with.
///
/// WHEN IT RUNS:
///   From <c>Program.cs</c>, AFTER <see cref="RoleSeeder.EnsureRolesCreatedAsync"/>
///   (so the Admin role already exists when we try to assign it) and BEFORE
///   <c>app.RunAsync()</c>.
///
/// IDEMPOTENCY:
///   The seeder queries <c>UserManager.GetUsersInRoleAsync("Admin")</c>.
///   If the returned list is non-empty, the seeder is a no-op. Safe to call
///   on every startup. After the first run, subsequent runs do nothing.
///
/// DEFAULT CREDENTIALS:
///   WorkerId (login): <c>ADMIN-0001</c>
///   Email:            <c>admin@takone.local</c>
///   Password:         <c>Admin@12345</c>
///
///   The password meets Identity's default complexity rules (8+ chars,
///   upper + lower + digit + non-alphanumeric, 4+ unique chars). The email
///   is marked confirmed so the user can sign in without an email loop.
///
/// SECURITY:
///   The default password is logged at WARNING level on first creation so
///   the developer notices it. CHANGE THE PASSWORD IMMEDIATELY after
///   first login (or, better, change it before exposing the app to anyone
///   else). For production, set the password via an environment variable
///   instead — see the TODO in <see cref="EnsureDefaultAdminAsync"/>.
/// </summary>
public static class DefaultAdminSeeder
{
    /// <summary>
    /// The default admin's login identifier (used as <c>UserName</c> on
    /// <c>ApplicationUser</c> and <c>WorkerId</c> on the Domain <c>User</c>).
    /// Hardcoded because this is a single, well-known bootstrap account.
    /// </summary>
    public const string DefaultWorkerId = "ADMIN-0001";

    /// <summary>
    /// The default admin's email. Uses the <c>.local</c> TLD so it can never
    /// accidentally route to a real mailbox.
    /// </summary>
    public const string DefaultEmail = "admin@takone.local";

    /// <summary>
    /// The default admin's initial password. MEETS Identity's default
    /// complexity rules. CHANGE THIS AFTER FIRST LOGIN.
    /// </summary>
    public const string DefaultPassword = "Admin@12345";

    /// <summary>
    /// Creates the default admin user if no Admin role users exist.
    /// </summary>
    /// <param name="rootProvider">
    /// The root <c>IServiceProvider</c> from <see cref="WebApplication.Services"/>.
    /// </param>
    public static async Task EnsureDefaultAdminAsync(IServiceProvider rootProvider)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);

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
        //    sure nobody has already grabbed the default WorkerId/Email —
        //    otherwise we'd collide on the unique index. This handles the
        //    "user was created but role-assignment failed" edge case.
        // ----------------------------------------------------------------
        var existingByWorkerId = await userManager.FindByNameAsync(DefaultWorkerId);
        if (existingByWorkerId is not null)
        {
            logger?.LogWarning(
                "DefaultAdminSeeder: a user with WorkerId '{WorkerId}' already " +
                "exists but is NOT in the Admin role. Skipping default admin " +
                "seeding to avoid duplicate. If this is unexpected, inspect " +
                "the AspNetUsers table manually.",
                DefaultWorkerId);
            return;
        }

        // ----------------------------------------------------------------
        // 4. Create the Domain User first. User.CreateStaff generates a new
        //    Guid; we'll use that same Guid when creating the ApplicationUser
        //    so the two share a primary key (the invariant documented on
        //    ApplicationUser).
        // ----------------------------------------------------------------
        var domainUser = User.CreateStaff(
            workerId: DefaultWorkerId,
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
            workerId: DefaultWorkerId,
            email: DefaultEmail,
            initialPassword: DefaultPassword,
            role: Roles.Admin,
            gender: Gender.Male);

        if (result.IsSuccess)
        {
            // Mark email as confirmed so the user can sign in without
            // an email loop. IUserAccountService.CreateIdentityAccountAsync
            // already does this in its documented contract, but we re-affirm
            // it here in case the implementation changes — without a confirmed
            // email, Identity's default sign-in flow may refuse to issue a
            // cookie if RequireConfirmedEmail is ever flipped to true in
            // appsettings.json.
            var appUser = await userManager.FindByIdAsync(domainUser.Id.ToString());
            if (appUser is not null && !appUser.EmailConfirmed)
            {
                var token = await userManager.GenerateEmailConfirmationTokenAsync(appUser);
                await userManager.ConfirmEmailAsync(appUser, token);
            }

            logger?.LogWarning(
                """
                DefaultAdminSeeder: CREATED default admin user.
                  WorkerId (login): {WorkerId}
                  Email:            {Email}
                  Password:         {Password}
                CHANGE THIS PASSWORD IMMEDIATELY after first login.
                """,
                DefaultWorkerId, DefaultEmail, DefaultPassword);
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
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;

namespace TakOne.Infrastructure.Identity;

/// <summary>
/// One-shot startup helper that ensures all 5 TakOne roles exist in the
/// <c>AspNetRoles</c> table. Called from <c>Program.cs</c> after the app
/// builds but before <c>app.Run()</c>.
///
/// WHY THIS IS NEEDED:
///   ASP.NET Identity does NOT seed roles automatically. If
///   <c>UserAccountService.CreateIdentityAccountAsync</c> calls
///   <c>UserManager.AddToRoleAsync(user, "Admin")</c> and the "Admin"
///   role doesn't exist yet, the call fails with "Role Admin does not
///   exist." This seeder creates all 5 roles (Admin, Manager, Employee,
///   Customer, ReadOnly — see <see cref="Roles"/>) on app startup so
///   user-creation commands work from the first request.
///
/// IDEMPOTENT:
///   Uses <c>RoleManager.RoleExistsAsync</c> to skip roles that already
///   exist. Safe to call on every startup — no-op after the first run.
///
/// WHY A STATIC METHOD (not IHostedService):
///   - The seeder must complete BEFORE the app starts accepting requests.
///     A static method awaited in Program.cs between Build() and Run()
///     gives us that ordering guarantee.
///   - IHostedService runs in the background after the host starts —
///     there's a race window where a request could arrive before the
///     roles exist. For Phase 1, the simpler synchronous approach is
///     better.
///   - If startup-time role seeding ever becomes too slow (e.g. when we
///     add a default admin user with a strong password hash), we can
///     switch to IHostedService with a "wait for seeding" gate.
///
/// WHY NOT USE EF CORE SEEDING (HasData in OnModelCreating):
///   - Identity roles require a stable Guid Id for HasData, but we use
///     the role name as the natural key. Generating deterministic Guids
///     from role names (e.g. GuidV5 from "Admin") is doable but adds
///     complexity for no real benefit.
///   - RoleManager.CreateAsync also normalizes the role name
///     (upper-cases it via ILookupNormalizer) and writes the
///     NormalizedName column. HasData doesn't do this normalization,
///     so we'd have to do it manually.
///   - The RoleManager approach is the canonical pattern recommended
///     by Microsoft for role seeding.
/// </summary>
public static class RoleSeeder
{
    /// <summary>
    /// All 5 standard TakOne roles, sourced from the
    /// <see cref="Roles"/> static class. Add new roles there first,
    /// then here if you ever add a 6th role.
    /// </summary>
    private static readonly string[] AllRoles =
    {
        Roles.Admin,
        Roles.Manager,
        Roles.Employee,
        Roles.Customer,
        Roles.ReadOnly,
    };

    /// <summary>
    /// Creates all 5 TakOne roles if they don't already exist.
    ///
    /// Call this from Program.cs:
    /// <code>
    /// var app = builder.Build();
    /// await RoleSeeder.EnsureRolesCreatedAsync(app.Services);
    /// // ... rest of middleware pipeline ...
    /// await app.RunAsync();
    /// </code>
    ///
    /// The method creates a DI scope internally (because
    /// <c>RoleManager</c> is Scoped, not Singleton), so the caller
    /// can pass the root <c>IServiceProvider</c> directly.
    /// </summary>
    /// <param name="rootProvider">
    /// The root <c>IServiceProvider</c> from <see cref="WebApplication.Services"/>.
    /// </param>
    public static async Task EnsureRolesCreatedAsync(IServiceProvider rootProvider)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);

        // Create a scope — RoleManager is Scoped (depends on the scoped
        // ApplicationDbContext). Resolving it from the root provider
        // directly would throw "cannot resolve Scoped from Singleton".
        using var scope = rootProvider.CreateScope();
        var scopedProvider = scope.ServiceProvider;

        var roleManager = scopedProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var logger = scopedProvider
            .GetService<ILogger<RoleSeeder>>();

        foreach (var roleName in AllRoles)
        {
            // RoleExistsAsync is idempotent — returns true if the role
            // already exists. We use it instead of FindByNameAsync
            // because RoleExistsAsync also goes through the
            // ILookupNormalizer (so "admin" and "Admin" are treated
            // as the same role).
            if (await roleManager.RoleExistsAsync(roleName))
            {
                continue;
            }

            // Create the role. The RoleManager normalizes the name
            // (upper-cases it) and writes both Name + NormalizedName
            // columns to AspNetRoles.
            var result = await roleManager.CreateAsync(
                new IdentityRole<Guid>(roleName));

            if (result.Succeeded)
            {
                logger?.LogInformation(
                    "RoleSeeder: created role '{Role}'.", roleName);
            }
            else
            {
                // If role creation fails, log loudly and move on. We
                // don't throw — throwing here would block the app from
                // starting, which is worse than a missing role (the
                // user-creation handler will fail with a clear error
                // later if the role is still missing).
                var errors = string.Join(", ",
                    result.Errors.Select(e => $"{e.Code}: {e.Description}"));
                logger?.LogError(
                    "RoleSeeder: FAILED to create role '{Role}'. Errors: {Errors}.",
                    roleName, errors);
            }
        }
    }
}
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TakOne.Infrastructure.Identity;
using TakOne.Infrastructure.Persistence;

namespace TakOne.WebUI.Diagnostics;

/// <summary>
/// Diagnostic helper to troubleshoot login issues.
/// Use this to verify user database state and password validation.
/// </summary>
public static class LoginDiagnostics
{
    /// <summary>
    /// Performs a comprehensive login diagnostics check.
    /// Logs all findings to help troubleshoot authentication issues.
    /// </summary>
    public static async Task RunDiagnosticsAsync(
        IServiceProvider serviceProvider,
        string workerId,
        string password)
    {
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var db = sp.GetRequiredService<ApplicationDbContext>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("LoginDiagnostics");

        logger.LogInformation("========== LOGIN DIAGNOSTICS START ==========");
        logger.LogInformation("WorkerId: {WorkerId}, Password Length: {Length}", workerId, password?.Length ?? 0);

        try
        {
            // 1. Check if ApplicationUser exists
            logger.LogInformation("1. Looking up ApplicationUser by WorkerId: {WorkerId}", workerId);
            var appUser = await userManager.FindByNameAsync(workerId);

            if (appUser is null)
            {
                logger.LogError("❌ ERROR: ApplicationUser not found for WorkerId: {WorkerId}", workerId);
                logger.LogInformation("Available users in database:");
                var allUsers = await userManager.Users.ToListAsync();
                foreach (var user in allUsers)
                {
                    logger.LogInformation("  - UserName: {UserName}, Id: {Id}, EmailConfirmed: {EmailConfirmed}, IsActive: {IsActive}",
                        user.UserName, user.Id, user.EmailConfirmed, user.IsActive);
                }
                return;
            }

            logger.LogInformation("✅ ApplicationUser found:");
            logger.LogInformation("   - Id: {Id}", appUser.Id);
            logger.LogInformation("   - UserName: {UserName}", appUser.UserName);
            logger.LogInformation("   - Email: {Email}", appUser.Email);
            logger.LogInformation("   - EmailConfirmed: {EmailConfirmed}", appUser.EmailConfirmed);
            logger.LogInformation("   - IsActive: {IsActive}", appUser.IsActive);
            logger.LogInformation("   - LockoutEnabled: {LockoutEnabled}", appUser.LockoutEnabled);
            logger.LogInformation("   - LockoutEnd: {LockoutEnd}", appUser.LockoutEnd);
            logger.LogInformation("   - AccessFailedCount: {AccessFailedCount}", appUser.AccessFailedCount);
            logger.LogInformation("   - PasswordHash exists: {HasPasswordHash}", !string.IsNullOrEmpty(appUser.PasswordHash));
            logger.LogInformation("   - SecurityStamp: {SecurityStamp}", appUser.SecurityStamp);

            // 2. Check if Domain User exists
            logger.LogInformation("2. Looking up Domain User by Id: {Id}", appUser.Id);
            var domainUser = await db.DomainUsers
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == appUser.Id);

            if (domainUser is null)
            {
                logger.LogError("❌ ERROR: Domain User not found for Id: {Id}", appUser.Id);
            }
            else
            {
                logger.LogInformation("✅ Domain User found:");
                logger.LogInformation("   - Id: {Id}", domainUser.Id);
                logger.LogInformation("   - WorkerId: {WorkerId}", domainUser.WorkerId);
                logger.LogInformation("   - FullName: {FullName}", domainUser.FullName);
                logger.LogInformation("   - GroupName: {GroupName}", domainUser.GroupName);
                logger.LogInformation("   - Gender: {Gender}", domainUser.Gender);
                logger.LogInformation("   - IsActive: {IsActive}", domainUser.IsActive);
            }

            // 3. Check if password hash is valid
            logger.LogInformation("3. Testing password validation");
            if (string.IsNullOrEmpty(appUser.PasswordHash))
            {
                logger.LogError("❌ ERROR: No password hash found for user");
                return;
            }

            var passwordHasher = sp.GetRequiredService<IPasswordHasher<ApplicationUser>>();
            var verificationResult = passwordHasher.VerifyHashedPassword(appUser, appUser.PasswordHash, password);

            logger.LogInformation("   Password verification result: {Result}", verificationResult);
            switch (verificationResult)
            {
                case PasswordVerificationResult.Success:
                    logger.LogInformation("✅ Password is CORRECT (Success)");
                    break;
                case PasswordVerificationResult.SuccessRehashNeeded:
                    logger.LogWarning("⚠️  Password is CORRECT but rehash is needed");
                    break;
                case PasswordVerificationResult.Failed:
                    logger.LogError("❌ Password is INCORRECT (verification failed)");
                    logger.LogError("   - Password provided: {Password}", password);
                    logger.LogError("   - Expected password (from DefaultAdminSeeder): MarkMccain2323!");
                    break;
            }

            // 4. Check user roles
            logger.LogInformation("4. Checking user roles");
            var roles = await userManager.GetRolesAsync(appUser);
            if (roles.Count == 0)
            {
                logger.LogError("❌ ERROR: User has no roles assigned");
            }
            else
            {
                logger.LogInformation("✅ User roles:");
                foreach (var role in roles)
                {
                    logger.LogInformation("   - {Role}", role);
                }
            }

            // 5. Check lockout status
            logger.LogInformation("5. Checking lockout status");
            if (appUser.LockoutEnabled && appUser.LockoutEnd.HasValue && appUser.LockoutEnd.Value > DateTimeOffset.UtcNow)
            {
                logger.LogError("❌ ERROR: User is locked out until: {LockoutEnd}", appUser.LockoutEnd);
            }
            else
            {
                logger.LogInformation("✅ User is not locked out");
            }

            logger.LogInformation("========== LOGIN DIAGNOSTICS END ==========");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during diagnostics");
        }
    }
}

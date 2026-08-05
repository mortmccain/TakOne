using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.Domain.Users;
using TakOne.Infrastructure.Identity;
using TakOne.Infrastructure.Persistence;
using TakOne.SharedKernel.Common;

namespace TakOne.Infrastructure.Services;

/// <summary>
/// Infrastructure implementation of <see cref="IUserAccountService"/>.
///
/// RESPONSIBILITY:
///   Bridge the framework-free Domain <c>User</c> aggregate to ASP.NET
///   Identity's <c>ApplicationUser</c>. Operations that the Domain cannot
///   model (passwords, email, Identity roles) live here.
///
/// SHARED-PK INVARIANT:
///   The Domain User and the ApplicationUser share the SAME primary key.
///   The handler creates the Domain User first (which generates a new Guid),
///   then calls <see cref="CreateIdentityAccountAsync"/> with that Guid — so
///   the ApplicationUser row uses the Domain User's Id as its own PK.
///
///   UserManager.CreateAsync normally GENERATES a new Guid for the Id.
///   We override that by explicitly setting <c>appUser.Id = userId</c> BEFORE
///   calling <c>CreateAsync</c>. EF Core + Identity will respect the
///   pre-set Id (it's just a primary key value, no different from any other).
///
/// TRANSACTIONALITY — HOW ATOMIC COMMITS ARE ACHIEVED:
///   This service shares the SAME <see cref="ApplicationDbContext"/> that the
///   repositories use (registered via DI as a scoped service — one instance
///   per HTTP request / Wolverine handler invocation). The intended handler
///   flow is:
///     1. userRepository.AddAsync(domainUser)              → tracks Domain User for INSERT
///     2. userAccountService.CreateIdentityAccountAsync()  → UserManager.CreateAsync
///                                                           calls Context.SaveChangesAsync
///                                                           internally (auto-save)
///     3. unitOfWork.SaveChangesAsync()                    → commits any remaining
///                                                           tracked changes
///
///   ASP.NET Identity's default EF Core <c>UserStore&lt;TUser&gt;</c>
///   implementation calls <c>Context.SaveChangesAsync()</c> INSIDE
///   <c>UserManager.CreateAsync</c>, <c>AddToRoleAsync</c>, <c>ResetPasswordAsync</c>,
///   etc. Without a transaction, the ApplicationUser row (and the AspNetUserRoles
///   row, etc.) would commit IMMEDIATELY at step 2 — NOT deferred to step 3 —
///   which would break the "same DbContext → same transaction" intuition and
///   produce orphan ApplicationUsers if the handler later fails.
///
///   THE FIX (wired in <c>AddTakOneInfrastructure</c>, Step 7e):
///   Wolverine's EF Core transactional middleware
///   (<c>opts.UseEntityFrameworkCoreTransactions()</c> +
///   <c>opts.Policies.AutoApplyTransactions()</c>) wraps every Wolverine handler
///   in an EF Core transaction. The middleware:
///     a. Calls <c>BeginTransactionAsync</c> on the DbContext before the handler
///        runs. From this point, ANY <c>SaveChangesAsync</c> call (including
///        Identity's auto-saves) writes its changes INSIDE the open transaction —
///        they are persisted to the database's transaction log but NOT committed.
///     b. After the handler returns successfully, calls <c>CommitAsync</c> —
///        all changes (Domain User, ApplicationUser, role assignment, outbox
///        entries) commit atomically.
///     c. If the handler throws, calls <c>RollbackAsync</c> — all changes roll
///        back together, including Identity rows that Identity "auto-saved".
///
///   This is cleaner than wrapping each Identity operation in a
///   <c>TransactionScope</c> (the option A approach we considered earlier):
///     - No <c>TransactionScopeAsyncFlowOption.Enabled</c> gotchas.
///     - No transaction-timeout tuning.
///     - The scope is owned by Wolverine, not by the handler — the handler
///       code stays clean.
///     - The Domain User INSERT and the ApplicationUser INSERT are in the
///       SAME transaction automatically, because both go through the same
///       scoped DbContext that Wolverine enrolled.
///
///   CAVEAT — handlers MUST be invoked via Wolverine:
///   This guarantee only applies to handlers running under Wolverine's
///   transactional middleware. If you ever call this service from a non-
///   Wolverine entry point (e.g. a Blazor component that resolves
///   IUserAccountService directly from DI), you MUST wrap the call in an
///   explicit <c>TransactionScope</c> or open an EF Core transaction on the
///   DbContext manually. In TakOne, all commands go through Wolverine, so
///   this caveat is currently moot — but it's worth knowing if you ever add
///   a non-Wolverine entry point.
///
///   This is the reason UserManager is constructed with our ApplicationDbContext
///   as its store, not a separate IdentityDbContext. (Wired in step 7e via
///   <c>AddIdentity&lt;ApplicationUser, IdentityRole&lt;Guid&gt;&gt;.AddEntityFrameworkStores&lt;ApplicationDbContext&gt;()</c>.)
///
/// ERROR SEMANTICS:
///   Identity operations return <c>IdentityResult</c>, which has a
///   <c>.Errors</c> collection of <c>IdentityError</c> objects (each with
///   Code + Description). We flatten these into a single semicolon-joined
///   string and return <c>Result.Failure(...)</c>. The handler surfaces the
///   string to the caller.
///
///   Identity error codes are things like "DuplicateUserName",
///   "PasswordTooShort", "InvalidEmail". They're localized by the
///   IdentityErrorDescriber — which we use as-is. If you ever need to map
///   specific codes to specific user-facing message keys, override the
///   describer in <c>AddTakOneInfrastructure</c>.
/// </summary>
public sealed class UserAccountService : IUserAccountService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<UserAccountService> _logger;

    public UserAccountService(
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext db,
        ILogger<UserAccountService> logger)
    {
        _userManager = userManager;
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Result> CreateIdentityAccountAsync(
        Guid userId,
        string workerId,
        string email,
        string initialPassword,
        string role,
        Gender gender,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // 1. Construct the ApplicationUser with the SHARED PK.
        //
        //    Setting Id = userId BEFORE CreateAsync overrides Identity's
        //    default "generate a new Guid" behavior. This is what makes the
        //    Domain User and ApplicationUser one-to-one by shared PK.
        //
        //    Gender is copied here as a DENORMALIZED value (see ApplicationUser
        //    class-level remark for the rationale — lets the admin user list
        //    render without a join to the Domain Users table). The Domain
        //    User remains the source of truth.
        // ------------------------------------------------------------------
        var appUser = new ApplicationUser
        {
            Id = userId,
            UserName = workerId,         // login identifier (matches Domain User.WorkerId)
            Email = email,
            EmailConfirmed = true,       // admin-created accounts skip email confirmation
            IsActive = true,             // mirrors Domain User.IsActive default
            Gender = gender,             // denormalized copy (Phase 0.5)
            SecurityStamp = Guid.NewGuid().ToString("N")
        };

        // SecurityStamp: Identity uses this to invalidate outstanding login
        // sessions when security-sensitive fields change (password, role).
        // We set it explicitly to a fresh Guid so it's deterministic and
        // traceable in logs. UserManager.CreateAsync would set one if we
        // left it null, but being explicit removes ambiguity.

        // ------------------------------------------------------------------
        // 2. Create the user with the password.
        //
        //    UserManager.CreateAsync hashes the password, validates
        //    uniqueness of UserName + Email, and INSERTs the row on the
        //    next SaveChanges. (It does NOT call SaveChanges itself — it
        //    calls the store's CreateAsync, which just queues the INSERT
        //    in the DbContext.)
        //
        //    Wait — actually, the default Identity EF store DOES call
        //    SaveChanges internally inside CreateAsync. So the ApplicationUser
        //    row commits IMMEDIATELY here, even if the handler later fails
        //    and never calls IUnitOfWork.SaveChangesAsync. This is a known
        //    Identity quirk.
        //
        //    Workaround: if the Domain User INSERT later fails, we manually
        //    DELETE the ApplicationUser row to roll back. The handler is
        //    responsible for catching that failure and calling a cleanup
        //    method on this service. (See CreateCustomerCommandHandler's
        //    transaction flow — currently it doesn't do this cleanup because
        //    in practice the Domain User INSERT can only fail on the unique
        //    WorkerId index, which we pre-check. But we should harden this
        //    in a future pass.)
        // ------------------------------------------------------------------
        var createResult = await _userManager.CreateAsync(appUser, initialPassword);
        if (!createResult.Succeeded)
        {
            var error = FlattenErrors(createResult);
            _logger.LogWarning(
                "UserAccountService.CreateIdentityAccountAsync: UserManager.CreateAsync failed " +
                "for userId {UserId}, workerId '{WorkerId}', email '{Email}'. Errors: {Errors}.",
                userId, workerId, email, error);
            return Result.Failure(error);
        }

        // ------------------------------------------------------------------
        // 3. Assign the role.
        //
        //    AddToRoleAsync:
        //      - Looks up the IdentityRole by name (throws if not found).
        //      - Inserts a row into AspNetUserRoles linking the user to the role.
        //
        //    If the role doesn't exist (e.g. the role seed hasn't run yet),
        //    this fails with "Role X does not exist." That's the most common
        //    production bug with this method — ensure role seeding runs on
        //    app startup (configured in step 7e or WebUI startup).
        // ------------------------------------------------------------------
        var roleResult = await _userManager.AddToRoleAsync(appUser, role);
        if (!roleResult.Succeeded)
        {
            var error = FlattenErrors(roleResult);
            _logger.LogWarning(
                "UserAccountService.CreateIdentityAccountAsync: AddToRoleAsync '{Role}' failed " +
                "for userId {UserId}. Errors: {Errors}.",
                role, userId, error);

            // Roll back the user creation. Otherwise we'd leave an
            // orphaned ApplicationUser with no role — the user could log in
            // but couldn't do anything (and the Domain User INSERT might
            // still go through, leaving a half-state).
            await _userManager.DeleteAsync(appUser);
            return Result.Failure(error);
        }

        _logger.LogInformation(
            "UserAccountService.CreateIdentityAccountAsync: Identity account created " +
            "for userId {UserId} (workerId '{WorkerId}', role '{Role}').",
            userId, workerId, role);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ResetPasswordAsync(
        Guid userId,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Load the ApplicationUser. We use the DbContext directly (not
        // UserManager.FindByIdAsync) for two reasons:
        //   1. FindByIdAsync takes a string, not a Guid — awkward.
        //   2. We want a TRACKED entity so that any property updates we make
        //      (none here, but future-proofing) are detected by the change
        //      tracker.
        // ------------------------------------------------------------------
        var appUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (appUser is null)
        {
            return Result.Failure($"Identity account for user '{userId}' was not found.");
        }

        // ------------------------------------------------------------------
        // UserManager.ResetPasswordAsync normally requires a token (issued
        // via UserManager.GeneratePasswordResetTokenAsync). That's the
        // user-driven "forgot password" flow. For an ADMIN-driven reset
        // (this method), we use RemovePasswordAsync + AddPasswordAsync —
        // which bypasses the token flow entirely.
        //
        // This is a deliberate choice: admin resets don't need the user to
        // click a link in an email. The admin is authenticated, and the
        // audit log captures who did it (the calling handler logs the actor).
        // ------------------------------------------------------------------
        var removeResult = await _userManager.RemovePasswordAsync(appUser);
        if (!removeResult.Succeeded)
        {
            var error = FlattenErrors(removeResult);
            _logger.LogWarning(
                "UserAccountService.ResetPasswordAsync: RemovePasswordAsync failed " +
                "for userId {UserId}. Errors: {Errors}.",
                userId, error);
            return Result.Failure(error);
        }

        var addResult = await _userManager.AddPasswordAsync(appUser, newPassword);
        if (!addResult.Succeeded)
        {
            var error = FlattenErrors(addResult);
            _logger.LogWarning(
                "UserAccountService.ResetPasswordAsync: AddPasswordAsync failed " +
                "for userId {UserId}. Errors: {Errors}.",
                userId, error);

            // We've removed the old password but couldn't set the new one —
            // the user now has NO password and CANNOT log in. Surface this
            // loudly so the admin knows to retry immediately. We do NOT
            // attempt to restore the old password (we don't have it; it was
            // hashed).
            return Result.Failure(
                $"Password reset FAILED for user '{userId}'. The old password was removed " +
                $"but the new password was rejected. Errors: {error}. " +
                $"The user cannot log in until this is resolved — please retry with a stronger password.");
        }

        _logger.LogInformation(
            "UserAccountService.ResetPasswordAsync: password reset for userId {UserId}.",
            userId);

        return Result.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// ENUMERATION DEFENSE: returns <c>null</c> if the user is not found OR
    /// if the user is deactivated. Both cases must look identical to the
    /// caller. See the interface docstring for the rationale.
    /// </remarks>
    public async Task<string?> GeneratePasswordResetTokenAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Look up by EMAIL (not WorkerId). The Forgot Password form collects
        // email because the user might not remember their WorkerId — that's
        // a common friction point in B2B apps.
        //
        // We use AsNoTracking — we don't intend to mutate the entity here.
        // UserManager.GeneratePasswordResetTokenAsync internally re-loads
        // the user via its own store, so a tracked entity wouldn't help.
        // ------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(email))
        {
            // Defensive — should be caught by form validation, but never
            // trust the boundary. Returning null here lets the caller show
            // the same generic message they'd show for "user not found".
            return null;
        }

        var appUser = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        // Enumeration defense: null for "not found" AND "deactivated".
        // An attacker probing for valid emails should get NO signal.
        if (appUser is null || !appUser.IsActive)
        {
            _logger.LogInformation(
                "UserAccountService.GeneratePasswordResetTokenAsync: " +
                "no active user found for email '{Email}' (returning null — caller shows generic message).",
                email);
            return null;
        }

        // ------------------------------------------------------------------
        // Generate the token. Identity's default token provider
        // (DataProtectorTokenProvider) issues a base64-encoded signed
        // string that encodes the user's SecurityStamp + a UTC timestamp.
        // The token is validated by UserManager.ResetPasswordAsync, which
        // checks the signature AND the timestamp (default 24h lifespan,
        // configurable via TokenLifespan in IdentityOptions.Tokens).
        // ------------------------------------------------------------------
        var token = await _userManager.GeneratePasswordResetTokenAsync(appUser);

        _logger.LogInformation(
            "UserAccountService.GeneratePasswordResetTokenAsync: " +
            "token generated for userId {UserId} (email '{Email}').",
            appUser.Id, email);

        return token;
    }

    /// <inheritdoc />
    public async Task<Result> ResetPasswordFromTokenAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Validate inputs. The caller (Razor page) already validates non-
        // empty + password complexity on the client side, but defense in
        // depth — never trust the boundary.
        // ------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(newPassword))
        {
            return Result.Failure("Invalid reset request.");
        }

        var appUser = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        // ENUMERATION DEFENSE: don't reveal "user not found" vs "token
        // invalid" — both return the same generic "link is invalid" message.
        // The user-facing copy in ResetPassword.razor uses Loc["Error_InvalidLink"]
        // for ALL failure cases except password-complexity violations.
        if (appUser is null || !appUser.IsActive)
        {
            _logger.LogWarning(
                "UserAccountService.ResetPasswordFromTokenAsync: " +
                "reset attempt for non-existent or deactivated email '{Email}'.",
                email);
            return Result.Failure("Invalid reset link.");
        }

        // ------------------------------------------------------------------
        // UserManager.ResetPasswordAsync:
        //   1. Validates the token signature + timestamp (throws if expired
        //      or tampered).
        //   2. Validates the new password against Identity's complexity
        //      rules (returns IdentityResult with PasswordTooShort /
        //      PasswordRequiresDigit / etc. on failure).
        //   3. On success, hashes the new password, updates the
        //      SecurityStamp (which invalidates outstanding login cookies
        //      — the user must re-authenticate everywhere), and saves.
        //
        // SecurityStamp invalidation is important here: if an attacker stole
        // the user's old password AND the user just reset their password,
        // the attacker's old session cookie is no longer valid.
        // ------------------------------------------------------------------
        var result = await _userManager.ResetPasswordAsync(appUser, token, newPassword);
        if (!result.Succeeded)
        {
            var error = FlattenErrors(result);
            _logger.LogWarning(
                "UserAccountService.ResetPasswordFromTokenAsync: " +
                "ResetPasswordAsync failed for userId {UserId} (email '{Email}'). Errors: {Errors}.",
                appUser.Id, email, error);

            // The errors collection may contain password-complexity errors
            // (PasswordTooShort, PasswordRequiresDigit, etc.) OR token-
            // validation errors (InvalidToken). We surface Identity's
            // descriptions verbatim — they're already localized by the
            // IdentityErrorDescriber. The page differentiates presentation
            // by checking whether the message starts with "InvalidToken"
            // or contains "password" — but we don't try to second-guess
            // Identity's wording here.
            return Result.Failure(error);
        }

        _logger.LogInformation(
            "UserAccountService.ResetPasswordFromTokenAsync: " +
            "password reset via token for userId {UserId} (email '{Email}').",
            appUser.Id, email);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> AssignRoleAsync(
        Guid userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        var appUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (appUser is null)
        {
            return Result.Failure($"Identity account for user '{userId}' was not found.");
        }

        // ------------------------------------------------------------------
        // Idempotency: if the user already has the role, IsInRoleAsync
        // returns true and we skip the AddToRoleAsync call. This avoids
        // a no-op INSERT into AspNetUserRoles (which would fail anyway
        // because Identity dedupes — but skipping is cleaner).
        // ------------------------------------------------------------------
        if (await _userManager.IsInRoleAsync(appUser, role))
        {
            _logger.LogDebug(
                "UserAccountService.AssignRoleAsync: user {UserId} already has role '{Role}' — no-op.",
                userId, role);
            return Result.Success();
        }

        var result = await _userManager.AddToRoleAsync(appUser, role);
        if (!result.Succeeded)
        {
            var error = FlattenErrors(result);
            _logger.LogWarning(
                "UserAccountService.AssignRoleAsync: AddToRoleAsync '{Role}' failed " +
                "for userId {UserId}. Errors: {Errors}.",
                role, userId, error);
            return Result.Failure(error);
        }

        _logger.LogInformation(
            "UserAccountService.AssignRoleAsync: role '{Role}' assigned to userId {UserId}.",
            role, userId);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> RemoveFromRoleAsync(
        Guid userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        var appUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (appUser is null)
        {
            return Result.Failure($"Identity account for user '{userId}' was not found.");
        }

        // Idempotency: if the user doesn't have the role, no-op.
        if (!await _userManager.IsInRoleAsync(appUser, role))
        {
            _logger.LogDebug(
                "UserAccountService.RemoveFromRoleAsync: user {UserId} does not have role '{Role}' — no-op.",
                userId, role);
            return Result.Success();
        }

        var result = await _userManager.RemoveFromRoleAsync(appUser, role);
        if (!result.Succeeded)
        {
            var error = FlattenErrors(result);
            _logger.LogWarning(
                "UserAccountService.RemoveFromRoleAsync: RemoveFromRoleAsync '{Role}' failed " +
                "for userId {UserId}. Errors: {Errors}.",
                role, userId, error);
            return Result.Failure(error);
        }

        _logger.LogInformation(
            "UserAccountService.RemoveFromRoleAsync: role '{Role}' removed from userId {UserId}.",
            role, userId);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetRolesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var appUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (appUser is null)
        {
            // Returning an empty list rather than throwing — the caller
            // (query handlers like GetUserByIdQueryHandler) treats "no roles"
            // as a valid state. A null User will surface elsewhere as a
            // "user not found" error.
            return Array.Empty<string>();
        }

        var roles = await _userManager.GetRolesAsync(appUser);
        return roles.ToList();
    }

    /// <inheritdoc />
    public async Task<Result> ChangePasswordAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // 1. Load the ApplicationUser (tracked — we'll mutate
        //    MustChangePassword on success).
        // ------------------------------------------------------------------
        var appUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (appUser is null)
        {
            _logger.LogWarning(
                "UserAccountService.ChangePasswordAsync: userId {UserId} not found.",
                userId);
            return Result.Failure($"Identity account for user '{userId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. UserManager.ChangePasswordAsync does three things atomically:
        //      a. Verifies currentPassword against the stored hash
        //         (returns PasswordMismatch error if wrong).
        //      b. Validates newPassword against the configured complexity
        //         rules (returns PasswordTooShort / PasswordRequiresDigit /
        //         etc. if it fails).
        //      c. Hashes newPassword and stores it, rotating the
        //         SecurityStamp (which invalidates other sessions on the
        //         next request — see SecurityStampValidator).
        //
        //    The Razor page is responsible for the "new password must not
        //    equal current password" check before calling this method
        //    (defense-in-depth; UserManager.ChangePasswordAsync will
        //    actually accept an identical password because Identity doesn't
        //    enforce password-history by default).
        // ------------------------------------------------------------------
        var result = await _userManager.ChangePasswordAsync(appUser, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            var error = FlattenErrors(result);
            _logger.LogWarning(
                "UserAccountService.ChangePasswordAsync: ChangePasswordAsync failed " +
                "for userId {UserId}. Errors: {Errors}.",
                userId, error);
            return Result.Failure(error);
        }

        // ------------------------------------------------------------------
        // 3. Clear the MustChangePassword flag. This is the second half of
        //    the "force one-time password change on first login" flow:
        //      - The flag was set to true by DefaultAdminSeeder (or by the
        //        one-time data migration that introduced the column).
        //      - Login.razor added a `must_change_password` claim to the
        //        auth cookie at sign-in.
        //      - The redirect middleware in Program.cs redirected the user
        //        here until they changed their password.
        //      - This method clears the flag on the user row.
        //      - The Razor page re-issues the auth cookie WITHOUT the
        //        claim after this method returns success.
        //
        //    We deliberately set this even if the flag was already false
        //    (e.g. for users created via the user-management UI who decided
        //    to change their password voluntarily). Setting false to false
        //    is a no-op and saves a branch.
        // ------------------------------------------------------------------
        if (appUser.MustChangePassword)
        {
            appUser.MustChangePassword = false;
            // UserManager.ChangePasswordAsync already called SaveChanges
            // internally (Identity's EF store auto-saves). But to be safe
            // and explicit — the MustChangePassword mutation happened
            // AFTER ChangePasswordAsync's internal save, so we need our
            // own save here to persist the flag clear.
            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "UserAccountService.ChangePasswordAsync: password changed for userId {UserId}.",
            userId);

        return Result.Success();
    }

    /// <summary>
    /// Flattens an <c>IdentityResult</c>'s Errors collection into a single
    /// semicolon-joined string for surface-friendly error messages.
    ///
    /// Each IdentityError has a Code (e.g. "DuplicateUserName",
    /// "PasswordTooShort") and a Description (the user-facing message,
    /// localized by the IdentityErrorDescriber).
    ///
    /// v6.2: We now drop the <c>{Code}: </c> prefix and join ONLY the
    /// <c>Description</c>, because the description is now localized via
    /// <c>TakOneIdentityErrorDescriber</c> + <c>IdentityErrorMessages.{culture}.resx</c>.
    /// Previously, when Identity returned English descriptions, we kept the
    /// code prefix so the message was grep-friendly by code in logs. Now
    /// that the description is in the user's UI culture, the raw English
    /// code is just noise — Persian users would see
    /// "PasswordRequiresNonAlphanumeric: رمز عبور باید..." which is
    /// confusing. The Code is still available in the structured log
    /// above (see the <c>Errors: {Errors}</c> parameter in the
    /// <c>_logger.LogWarning</c> call that precedes the
    /// <c>Result.Failure</c> return), so log greppability is preserved.
    /// </summary>
    private static string FlattenErrors(IdentityResult result)
    {
        if (result.Errors is null)
        {
            return "Unknown Identity failure.";
        }

        return string.Join("; ", result.Errors.Select(e => e.Description));
    }
}
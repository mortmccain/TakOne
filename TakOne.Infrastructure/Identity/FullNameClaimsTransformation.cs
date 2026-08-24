using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TakOne.Infrastructure.Persistence;

namespace TakOne.Infrastructure.Identity;

/// <summary>
/// Enriches every authenticated <see cref="ClaimsPrincipal"/> with the CURRENT
/// <c>FullName</c> claim, read from the Domain Users table — AND rejects
/// principals whose <c>IsActive</c> flag is <c>false</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>RESPONSIBILITY #1 — the "stale cookie" FullName bug:</b>
/// </para>
/// <para>
/// <c>Login.razor</c> bakes the <c>FullName</c> claim into the auth cookie at
/// sign-in time by reading the Domain User. If the Domain User lookup fails
/// transiently at that moment (DB blip, race with seeder, etc.), the fallback
/// <c>domainUser?.FullName ?? appUser.UserName</c> silently bakes the USERNAME
/// (e.g. "ADMIN-0001") into the cookie as the FullName. Every subsequent
/// request then reads the stale wrong value from the cookie — the user sees
/// "ADMIN-0001" instead of their real name until they log out and back in.
/// </para>
/// <para>
/// This also affects auditing: command handlers read
/// <c>ICurrentUserService.FullName</c> (which reads the claim) to record WHO
/// performed an action. A stale FullName means the audit trail is wrong.
/// </para>
/// <para>
/// <b>HOW THIS FIXES IT:</b>
/// <c>IClaimsTransformation</c> runs on EVERY authenticated request — AFTER
/// the cookie is decrypted but BEFORE the request reaches the endpoint. We
/// read the current FullName from the DB and replace the claim if it's missing
/// or stale. The cookie itself is NOT modified (that would require a
/// re-sign-in); only the in-memory principal for THIS request is enriched.
/// </para>
/// <para>
/// <b>RESPONSIBILITY #2 — the "deactivated user still logged in" bug:</b>
/// </para>
/// <para>
/// The <c>IsActive</c> check in <c>Login.razor</c> only runs at LOGIN time.
/// Once the cookie is issued (valid for <c>CookieExpiryHours</c>, default 8h),
/// the user stays "logged in" even if an admin deactivates them mid-session.
/// ASP.NET Identity's <c>SecurityStampValidator</c> would normally catch this,
/// but (a) it isn't configured in this app, and (b) <c>DeactivateUserCommandHandler</c>
/// doesn't update the <c>SecurityStamp</c> — so even if it WERE configured, it
/// wouldn't trigger on deactivation.
/// </para>
/// <para>
/// <b>HOW THIS FIXES IT:</b>
/// On every authenticated request, we fetch <c>IsActive</c> from the
/// ApplicationUser table (cached for 30s). If <c>IsActive == false</c>, we
/// return a new <see cref="ClaimsPrincipal"/> with NO identities — which makes
/// <c>principal.Identity.IsAuthenticated</c> return <c>false</c>. The
/// authorization middleware then treats the request as anonymous and
/// redirects to /Account/Login. The user sees the login page (no explicit
/// "you were deactivated" message — they just appear logged out). When they
/// try to log back in, the <c>Login.razor</c> pre-check catches the
/// deactivation and shows "invalid credentials".
/// </para>
/// <para>
/// <b>WHY NOT A SEPARATE MIDDLEWARE?</b>
/// A dedicated <c>DeactivatedUserMiddleware</c> would be architecturally
/// cleaner (separation of concerns), but this transformation already runs on
/// every authenticated request, already has the scoped DbContext + cache
/// pattern set up, and already pays the DB-lookup cost. Adding a second
/// component would double the DB hits. Folding both responsibilities into one
/// lookup is the pragmatic choice.
/// </para>
/// <para>
/// <b>CACHING:</b>
/// Without caching, this would hit the DB on every single authenticated
/// request (page navigation, API call, SignalR message, etc.). We cache the
/// FullName + IsActive pair per-user in <see cref="IMemoryCache"/> for 30
/// seconds. This means:
/// <list type="bullet">
///   <item>At most ONE DB lookup per user per 30 seconds.</item>
///   <item>If the admin deactivates a user, that user's existing session is
///       rejected within 30 seconds (on their next request after the cache
///       expires) — no re-login required to enforce the deactivation.</item>
///   <item>If the cache entry is evicted (memory pressure), the next request
///       simply re-reads from the DB — correctness is never compromised.</item>
/// </list>
/// </para>
/// <para>
/// <b>REGISTRATION:</b>
/// Registered as <c>AddScoped&lt;IClaimsTransformation,
/// FullNameClaimsTransformation&gt;()</c> in
/// <c>ServiceCollectionExtensions.AddTakOneInfrastructure</c>. Scoped because
/// it depends on the scoped <see cref="ApplicationDbContext"/> (via
/// <see cref="IServiceScopeFactory"/>).
/// </para>
/// </remarks>
public sealed class FullNameClaimsTransformation : IClaimsTransformation
{
    /// <summary>
    /// Cache TTL — 30 seconds. Balances freshness (admin name changes +
    /// deactivations visible within 30s) against DB load (at most one lookup
    /// per user per 30s).
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<FullNameClaimsTransformation> _logger;

    public FullNameClaimsTransformation(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<FullNameClaimsTransformation> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Enrich the principal with the current FullName claim AND reject
    /// deactivated users. Called by the auth middleware on every authenticated
    /// request.
    /// </summary>
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        // Bail out fast for unauthenticated requests — no work to do.
        if (principal.Identity?.IsAuthenticated != true)
            return principal;

        // We only transform principals from the Identity cookie scheme. Other
        // schemes (e.g. a future API token scheme) wouldn't have a
        // NameIdentifier claim in the format we expect.
        var userIdString = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userId))
            return principal;

        // ── Fetch the current FullName + IsActive (from cache or DB) ──
        var cacheKey = $"user_state:{userId}";
        if (!_cache.TryGetValue(cacheKey, out UserState? state) || state is null)
        {
            // IMemoryCache doesn't resolve scoped services, so we create a
            // scope to get a fresh ApplicationDbContext for this lookup.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Project just the columns we need — avoids loading the entire
            // User entity. AsNoTracking because we never mutate this row here.
            //
            // We query ApplicationUser (not DomainUser) for IsActive AND
            // MustChangePassword because those are Identity-side flags.
            // The FullName comes from DomainUser as before.
            var fetched = await (
                from du in db.DomainUsers.AsNoTracking().Where(u => u.Id == userId)
                join au in db.Users.AsNoTracking() on du.Id equals au.Id
                select new { du.FullName, au.IsActive, au.MustChangePassword }
            ).FirstOrDefaultAsync();

            if (fetched is null)
            {
                // DomainUser OR ApplicationUser row is missing — shouldn't
                // happen (they share a PK), but defense-in-depth.
                _logger.LogWarning(
                    "FullNameClaimsTransformation: DomainUser/ApplicationUser not found for UserId={UserId}. " +
                    "Falling back to UserName claim for this request (NOT cached). " +
                    "This usually means the row is missing — check the seeder.",
                    userId);
                var fallbackName = principal.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;
                // Return early WITHOUT caching — next request will re-check the DB.
                return EnrichPrincipal(principal, fallbackName);
            }

            state = new UserState(fetched.FullName, fetched.IsActive, fetched.MustChangePassword);
            _cache.Set(cacheKey, state, CacheTtl);
        }

        // ── Reject deactivated users ──
        // Return a new ClaimsPrincipal with NO identities. This makes
        // principal.Identity.IsAuthenticated return false, so the authorization
        // middleware treats the request as anonymous and redirects to
        // /Account/Login. The cookie itself is NOT cleared (that would require
        // SignInManager.SignOutAsync, which IClaimsTransformation can't call),
        // but the user can't access anything until they re-authenticate — and
        // when they try, Login.razor's IsActive pre-check will catch them.
        if (!state.IsActive)
        {
            _logger.LogInformation(
                "FullNameClaimsTransformation: rejecting deactivated user UserId={UserId}. " +
                "Existing cookie is being ignored for this request (user appears anonymous).",
                userId);
            return new ClaimsPrincipal();
        }

        // ── Sync the must_change_password claim with the DB ──
        // The claim on the cookie can be stale — e.g. the MustChangePassword
        // flag was set to true on the DB row AFTER the cookie was issued
        // (by an admin resetting the password), or the flag was cleared on
        // the DB row but the cookie still carries the claim (user changed
        // their password in another tab). We sync the claim here so the
        // MustChangePasswordRedirectMiddleware (in Program.cs) sees the
        // CURRENT DB state, not a stale cookie claim.
        //
        // If MustChangePassword is true on the DB: ADD the claim (if missing).
        // If MustChangePassword is false on the DB: REMOVE the claim (if present).
        // This runs on every authenticated request (within the 30s cache),
        // so the middleware never sees a stale claim for more than 30s.
        var mcpClaim = principal.FindFirst("must_change_password");
        if (state.MustChangePassword && mcpClaim is null)
        {
            // DB says the user must change their password, but the cookie
            // doesn't have the claim. Add it to the transformed principal
            // so the middleware redirects them to /Account/ChangePassword.
            var enriched = EnrichPrincipalWithClaim(principal, "must_change_password", "true");
            _logger.LogInformation(
                "FullNameClaimsTransformation: added must_change_password claim for UserId={UserId} " +
                "(DB flag is true but cookie claim was missing). The MustChangePassword middleware will redirect.",
                userId);
            return enriched;
        }
        if (!state.MustChangePassword && mcpClaim is not null)
        {
            // DB says the user has already changed their password, but the
            // cookie still carries the stale claim. Strip it from the
            // transformed principal so the middleware doesn't redirect.
            var stripped = StripClaimFromPrincipal(principal, "must_change_password");
            _logger.LogInformation(
                "FullNameClaimsTransformation: stripped stale must_change_password claim for UserId={UserId} " +
                "(DB flag is false but cookie still had the claim).",
                userId);
            return stripped;
        }

        return EnrichPrincipal(principal, state.FullName);
    }

    /// <summary>
    /// Cached per-user state: FullName (for claim enrichment) + IsActive
    /// (for deactivation enforcement) + MustChangePassword (for forced-
    /// password-change middleware sync).
    /// </summary>
    private sealed record UserState(string FullName, bool IsActive, bool MustChangePassword);

    /// <summary>
    /// Returns a new <see cref="ClaimsPrincipal"/> with the <c>FullName</c>
    /// claim set to <paramref name="fullName"/>. If the principal already has
    /// the correct FullName, it's returned unchanged (avoids needless cloning).
    /// </summary>
    private static ClaimsPrincipal EnrichPrincipal(ClaimsPrincipal principal, string fullName)
    {
        if (string.IsNullOrEmpty(fullName))
            return principal;

        var existing = principal.FindFirst("FullName");
        if (existing is not null && existing.Value == fullName)
            return principal; // already correct — no clone needed

        // Clone the first identity (the cookie identity) and replace the
        // FullName claim. We MUST clone — modifying the original identity
        // would mutate the cached cookie principal, which is shared across
        // requests and must remain immutable.
        var identity = principal.Identities.FirstOrDefault();
        if (identity is null)
            return principal;

        var clone = identity.Clone();
        if (existing is not null)
            clone.RemoveClaim(existing);
        clone.AddClaim(new Claim("FullName", fullName));

        // Return a new principal with the cloned identity plus any other
        // identities the original principal had (rare, but preserves them).
        var newPrincipal = new ClaimsPrincipal(clone);
        foreach (var otherIdentity in principal.Identities.Skip(1))
            newPrincipal.AddIdentity(otherIdentity);

        return newPrincipal;
    }

    /// <summary>
    /// Returns a new <see cref="ClaimsPrincipal"/> with an ADDITIONAL claim
    /// of the given type + value. Used to add <c>must_change_password</c>
    /// when the DB says the flag is true but the cookie doesn't carry it.
    /// </summary>
    private static ClaimsPrincipal EnrichPrincipalWithClaim(
        ClaimsPrincipal principal, string claimType, string claimValue)
    {
        var identity = principal.Identities.FirstOrDefault();
        if (identity is null)
            return principal;

        var clone = identity.Clone();
        clone.AddClaim(new Claim(claimType, claimValue));

        var newPrincipal = new ClaimsPrincipal(clone);
        foreach (var otherIdentity in principal.Identities.Skip(1))
            newPrincipal.AddIdentity(otherIdentity);

        return newPrincipal;
    }

    /// <summary>
    /// Returns a new <see cref="ClaimsPrincipal"/> with ALL claims of the
    /// given type REMOVED. Used to strip a stale <c>must_change_password</c>
    /// claim when the DB says the flag is false but the cookie still carries it.
    /// </summary>
    private static ClaimsPrincipal StripClaimFromPrincipal(
        ClaimsPrincipal principal, string claimType)
    {
        var identity = principal.Identities.FirstOrDefault();
        if (identity is null)
            return principal;

        var claims = identity.Claims.Where(c => c.Type != claimType).ToList();
        var clone = new ClaimsIdentity(claims, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType);

        var newPrincipal = new ClaimsPrincipal(clone);
        foreach (var otherIdentity in principal.Identities.Skip(1))
            newPrincipal.AddIdentity(otherIdentity);

        return newPrincipal;
    }
}

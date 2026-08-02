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
/// <c>FullName</c> claim, read from the Domain Users table.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY THIS EXISTS — the "stale cookie" bug:</b>
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
/// <b>CACHING:</b>
/// Without caching, this would hit the DB on every single authenticated
/// request (page navigation, API call, SignalR message, etc.). We cache the
/// FullName per-user in <see cref="IMemoryCache"/> for 30 seconds. This
/// means:
/// <list type="bullet">
///   <item>At most ONE DB lookup per user per 30 seconds.</item>
///   <item>If the admin changes a user's name, that user sees the new name
///       within 30 seconds (on their next request after the cache expires)
///       — no re-login required.</item>
///   <item>If the cache entry is evicted (memory pressure), the next request
///       simply re-reads from the DB — correctness is never compromised.</item>
/// </list>
/// </para>
/// <para>
/// <b>WHY NOT JUST FIX LOGIN.razor?</b>
/// Fixing the Login fallback (to not bake "ADMIN-0001") would prevent NEW
/// stale cookies, but it wouldn't fix EXISTING stale cookies — users with a
/// stale cookie would still see the wrong name until they re-login. This
/// transformation fixes both new AND existing stale cookies, and also keeps
/// the FullName current if the admin changes a user's name after login.
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
    /// Cache TTL — 30 seconds. Balances freshness (admin name changes visible
    /// within 30s) against DB load (at most one lookup per user per 30s).
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
    /// Enrich the principal with the current FullName claim. Called by the
    /// auth middleware on every authenticated request.
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

        // ── Fetch the current FullName (from cache or DB) ──────────────
        var cacheKey = $"fullname:{userId}";
        if (!_cache.TryGetValue(cacheKey, out string? currentFullName) || string.IsNullOrEmpty(currentFullName))
        {
            // IMemoryCache doesn't resolve scoped services, so we create a
            // scope to get a fresh ApplicationDbContext for this lookup.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Project just the FullName column — avoids loading the entire
            // User entity. AsNoTracking because we never mutate this row here.
            currentFullName = await db.DomainUsers
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync();

            // If the Domain User row doesn't exist (shouldn't happen, but
            // defense-in-depth), fall back to the UserName claim rather than
            // baking an empty string. We DON'T cache this fallback so the
            // next request will re-check the DB (in case the row appears
            // later — e.g. the seeder hadn't completed yet).
            if (string.IsNullOrEmpty(currentFullName))
            {
                _logger.LogWarning(
                    "FullNameClaimsTransformation: DomainUser not found for UserId={UserId}. " +
                    "Falling back to UserName claim for this request (NOT cached). " +
                    "This usually means the DomainUser row is missing — check the seeder.",
                    userId);
                currentFullName = principal.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty;
                // Return early WITHOUT caching — next request will re-check the DB.
                return EnrichPrincipal(principal, currentFullName);
            }

            // Cache the successful lookup.
            _cache.Set(cacheKey, currentFullName, CacheTtl);
        }

        return EnrichPrincipal(principal, currentFullName);
    }

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
}
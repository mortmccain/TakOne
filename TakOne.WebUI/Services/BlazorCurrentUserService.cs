using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using TakOne.Application.Common.Interfaces;

namespace TakOne.WebUI.Services;

/// <summary>
/// Blazor Server implementation of <see cref="ICurrentUserService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE: Scoped.</b> One instance per Blazor circuit. The circuit is
/// established on first load and persists across navigation within the
/// same browser tab.
/// </para>
/// <para>
/// <b>ISSUE #08 — CIRCUIT-SAFE USER RESOLUTION (CRITICAL FIX):</b>
/// </para>
/// <para>
/// The PREVIOUS implementation read from
/// <c>IHttpContextAccessor.HttpContext?.User</c> on every property access.
/// In Blazor Server (InteractiveServer render mode), <c>HttpContext</c>
/// is ONLY available during the initial prerender request. Once the
/// circuit is established, <c>HttpContext</c> is <c>null</c> — which
/// means <c>IsAuthenticated</c> returned <c>false</c> and
/// <c>IsInRole(...)</c> returned <c>false</c> for ALL circuit operations
/// (button clicks, <c>OnInitializedAsync</c> second run, etc.).
/// </para>
/// <para>
/// This broke <c>AuthorizationMiddleware</c>: every
/// <c>[RequireRoles]</c>-protected command invoked from a Blazor page
/// (i.e. via <c>IMessageBus.InvokeAsync</c> in a button-click handler)
/// was rejected with "Authentication required." — even though the user
/// WAS logged in. The developer worked around this by NOT decorating
/// circuit-called queries with <c>[RequireRoles]</c> (see the comment
/// on <c>GetAllGroupNamesQuery</c>), which is exactly the fail-open
/// pattern that Issue #08 is about.
/// </para>
/// <para>
/// THE FIX: resolve the <c>ClaimsPrincipal</c> from
/// <see cref="AuthenticationStateProvider"/> when <c>HttpContext</c> is
/// null. <c>AuthenticationStateProvider</c> works in BOTH contexts:
/// </para>
/// <list type="bullet">
///   <item>During prerender: <c>ServerAuthenticationStateProvider</c> reads
///         from <c>HttpContext.User</c> (same as before).</item>
///   <item>During circuit: <c>ServerAuthenticationStateProvider</c> reads
///         from the circuit's cached auth state (set when the circuit was
///         established from the initial HTTP request).</item>
/// </list>
/// <para>
/// <b>SYNC-OVER-ASYNC SAFETY:</b> The <c>ICurrentUserService</c> interface
/// is synchronous (a known deferral — see the "Concern B" note in the
/// roadmap). <c>AuthenticationStateProvider.GetAuthenticationStateAsync()</c>
/// is async by signature, but in Blazor Server the underlying
/// <c>ServerAuthenticationStateProvider</c> completes SYNCHRONOUSLY (it
/// reads from in-memory circuit state — no I/O, no network). Calling
/// <c>.GetAwaiter().GetResult()</c> is therefore safe: there is no thread
/// to deadlock and no real async work to block on.
/// </para>
/// <para>
/// <b>CACHING:</b> The resolved <c>ClaimsPrincipal</c> is cached on first
/// access. Cookie auth doesn't change mid-circuit (the auth cookie is set
/// on the HTTP response, not readable from inside the circuit), so caching
/// is correct and avoids repeated <c>GetAuthenticationStateAsync</c> calls.
/// </para>
/// <para>
/// <b>DEFENSE IN DEPTH:</b> The <c>AuthorizationMiddleware</c> is the
/// first line of defense (rejects unauthenticated/Unauthorized calls before
/// the handler runs). The <c>IsAuthenticated</c> property here is a SECOND
/// check that handlers can use for defense-in-depth.
/// </para>
/// </remarks>
public sealed class BlazorCurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AuthenticationStateProvider _authStateProvider;

    // Lazy-cached ClaimsPrincipal. Null means "not yet resolved or
    // anonymous". _isResolved prevents repeated resolution attempts.
    private ClaimsPrincipal? _cachedUser;
    private bool _isResolved;

    public BlazorCurrentUserService(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationStateProvider authStateProvider)
    {
        _httpContextAccessor = httpContextAccessor;
        _authStateProvider = authStateProvider;
    }

    public Guid UserId
    {
        get
        {
            var user = GetCurrentUser();
            if (user?.Identity?.IsAuthenticated != true) return Guid.Empty;

            // ASP.NET Identity's ClaimTypes.NameIdentifier holds the user's
            // Guid Id (the ApplicationUser.Id we share with the Domain User).
            var claim = user.FindFirst(ClaimTypes.NameIdentifier);
            return claim is not null && Guid.TryParse(claim.Value, out var id) ? id : Guid.Empty;
        }
    }

    public string WorkerId
    {
        get
        {
            var user = GetCurrentUser();
            return user?.Identity?.IsAuthenticated == true
                ? (user.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty)
                : string.Empty;
        }
    }

    public string FullName
    {
        get
        {
            var user = GetCurrentUser();
            // We store the display name as a custom "FullName" claim at login
            // time (added by Login.razor in Phase 1). If missing, fall back
            // to the WorkerId (which is always present).
            return user?.Identity?.IsAuthenticated == true
                ? (user.FindFirst("FullName")?.Value ?? WorkerId)
                : string.Empty;
        }
    }

    /// <summary>
    /// The customer group Id of the authenticated user, or null if the user
    /// has no group (staff users). Sourced from the "GroupId" claim set at
    /// login time.
    ///
    /// STALE-CLAIM WARNING (Salary feature, Step 3):
    ///   This value is a snapshot from login time. If an admin reassigns
    ///   the user's group AFTER they logged in, this claim reflects the
    ///   OLD group. Handlers that need the CURRENT group (e.g. for
    ///   purchase-limit or salary-budget checks) MUST re-load the user
    ///   from the repository via <c>IUserRepository.GetByIdAsync</c>.
    ///   The claim is only for fast-path UI gating — never for
    ///   authoritative budget enforcement.
    /// </summary>
    public Guid? GroupId
    {
        get
        {
            var user = GetCurrentUser();
            if (user?.Identity?.IsAuthenticated != true) return null;

            var claim = user.FindFirst("GroupId");
            if (claim is null || !Guid.TryParse(claim.Value, out var groupId))
                return null;

            return groupId;
        }
    }

    /// <summary>
    /// Reads the "Gender" claim (set at login time by Login.razor from the
    /// Domain User's Gender). Returns "Male", "Female", or null if the claim
    /// is missing (e.g. older sessions created before the Gender claim was
    /// added). Used by the dashboard greeting to pick Mr./Ms.
    /// </summary>
    public string? Gender
    {
        get
        {
            var user = GetCurrentUser();
            return user?.Identity?.IsAuthenticated == true
                ? user.FindFirst("Gender")?.Value
                : null;
        }
    }

    public bool IsAuthenticated =>
        GetCurrentUser()?.Identity?.IsAuthenticated == true;

    public bool IsInRole(string role)
    {
        var user = GetCurrentUser();
        return user?.IsInRole(role) == true;
    }

    /// <summary>
    /// Resolves the current <see cref="ClaimsPrincipal"/> from the most
    /// appropriate source. During prerender (HTTP request in flight),
    /// reads from <see cref="IHttpContextAccessor.HttpContext"/>. During
    /// circuit operations (no HTTP context), reads from
    /// <see cref="AuthenticationStateProvider"/>. The result is cached
    /// for the lifetime of this Scoped instance.
    /// </summary>
    private ClaimsPrincipal? GetCurrentUser()
    {
        if (_isResolved) return _cachedUser;

        // Strategy 1: HttpContext (works during prerender and minimal-API
        // endpoints). This is the FAST path — no async involved.
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.User is not null)
        {
            _cachedUser = httpContext.User;
            _isResolved = true;
            return _cachedUser;
        }

        // Strategy 2: AuthenticationStateProvider (works during Blazor
        // circuit operations where HttpContext is null). This is the
        // sync-over-async path — safe in Blazor Server because
        // ServerAuthenticationStateProvider completes synchronously (reads
        // from in-memory circuit state, no real I/O).
        try
        {
            var authState = _authStateProvider
                .GetAuthenticationStateAsync()
                .GetAwaiter()
                .GetResult();
            _cachedUser = authState.User;
        }
        catch
        {
            // If AuthenticationStateProvider throws (e.g. during app
            // startup before the circuit is established), treat as
            // anonymous. The middleware will reject the call — safer
            // than propagating an exception through Wolverine's pipeline.
            _cachedUser = null;
        }

        _isResolved = true;
        return _cachedUser;
    }
}
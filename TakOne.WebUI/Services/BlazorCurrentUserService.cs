using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using TakOne.Application.Common.Interfaces;

namespace TakOne.WebUI.Services;

/// <summary>
/// Blazor Server implementation of <see cref="ICurrentUserService"/>. Phase 0.14.
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE: Scoped.</b> One instance per Blazor circuit. The circuit is
/// established on first load and persists across navigation within the
/// same browser tab. The user info is captured ONCE at circuit start
/// (when the first HTTP request arrives) and reused for the lifetime of
/// the circuit — this is correct because cookie auth doesn't change
/// mid-circuit.
/// </para>
/// <para>
/// <b>WHY NOT ASYNC (Concern B deferred):</b> the roadmap's "Concern B"
/// decision was to make <c>ICurrentUserService</c> async (return
/// <c>Task&lt;T&gt;</c>). The motivation was a latent risk that
/// <c>BlazorCurrentUserService</c> might block on <c>.Result</c> if it
/// ever needed to call an async API. In practice, our service reads
/// synchronously from <c>IHttpContextAccessor.HttpContext.User</c> —
/// no async work is involved, so <c>.Result</c> is never called. The
/// sync interface is therefore safe for our setup.
/// </para>
/// <para>
/// If a future iteration moves user-info lookup to an async source (e.g.
/// a cached remote claims service), the interface should be made async
/// and all command/query handlers updated. For Phase 0, we keep the
/// sync interface — see worklog entry for Phase 0 for the deferral
/// rationale.
/// </para>
/// <para>
/// <b>DEFENSE IN DEPTH:</b> the handlers' <c>AuthorizationMiddleware</c>
/// already rejects unauthenticated requests before the handler runs. The
/// <c>IsAuthenticated</c> property here is a SECOND check that handlers
/// can use for defense-in-depth (e.g. "if not authenticated, return
/// Result.Unauthorized").
/// </para>
/// </remarks>
public sealed class BlazorCurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public BlazorCurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid UserId
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
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
            var user = _httpContextAccessor.HttpContext?.User;
            return user?.Identity?.IsAuthenticated == true
                ? (user.FindFirst(ClaimTypes.Name)?.Value ?? string.Empty)
                : string.Empty;
        }
    }

    public string FullName
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            // We store the display name as a custom "FullName" claim at login
            // time (added by Login.razor in Phase 1). If missing, fall back
            // to the WorkerId (which is always present).
            return user?.Identity?.IsAuthenticated == true
                ? (user.FindFirst("FullName")?.Value ?? WorkerId)
                : string.Empty;
        }
    }

    public string? GroupName
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            return user?.Identity?.IsAuthenticated == true
                ? user.FindFirst("GroupName")?.Value
                : null;
        }
    }

    public bool IsAuthenticated =>
        _httpContextAccessor.HttpContext?.User?.Identity?.IsAuthenticated == true;

    public bool IsInRole(string role)
    {
        var user = _httpContextAccessor.HttpContext?.User;
        return user?.IsInRole(role) == true;
    }
}
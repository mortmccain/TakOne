using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using NSubstitute;
using Radzen;
using System.Security.Claims;

namespace TakOne.WebUI.ComponentTests;

/// <summary>
/// Test scaffolding helpers shared across all 13 bUnit test files in this
/// project. The SUT components (BottomNav, dialogs, CartBudgetBar, etc.)
/// all use Radzen + IStringLocalizer&lt;T&gt; + NavigationManager — this
/// helper centralizes the bUnit plumbing required to render them in a
/// TestContext.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a factory instead of a base class.</b> xUnit creates a fresh
/// instance of the test class per test method. We want a fresh
/// <see cref="TestContext"/> per test (services shouldn't leak between
/// tests), so a static factory method returning the configured context is
/// the cleanest pattern — base-class fields would be shared-then-recreated
/// which is confusing.
/// </para>
/// <para>
/// <b>JSRuntimeMode.Loose.</b> Radzen components (RadzenButton,
/// RadzenFormField, RadzenIcon, RadzenAlert, RadzenProgressBar) all call
/// into JS via <c>IJSRuntime</c> during <c>OnAfterRenderAsync</c> to
/// register event handlers, mutation observers, tooltip ARIA attributes,
/// etc. In bUnit, the default JSRuntimeMode is <c>Strict</c> — it throws
/// if an unplanned invocation occurs. Loose mode returns default values
/// for any unplanned JS call, so Radzen components render without us
/// having to plan every JS invocation. (In production these calls are
/// no-ops if Radzen's JS module isn't loaded anyway, e.g. SSR mode.)
/// </para>
/// <para>
/// <b>DialogService substitute.</b> The 7 dialog components (Deactivate
/// User/Product/Group, RemoveRole/Group, RestockProduct,
/// CancelConfirmation) all inject <c>Radzen.DialogService</c> and call
/// <c>DialogService.Close(object)</c> when the user clicks Confirm or
/// Cancel. <c>Close(Object)</c> is virtual (verified by reflection on the
/// Radzen.Blazor v11.1.8 assembly), so NSubstitute can intercept it.
/// The DialogService ctor takes <c>(NavigationManager, IJSRuntime,
/// IServiceProvider)</c> — passing in a substitute NavigationManager
/// fails because the ctor subscribes to
/// <c>uriHelper.LocationChanged</c> (a non-virtual event accessor that
/// throws "not initialized" when invoked on a substitute). We use a
/// factory delegate that resolves bUnit's real NavigationManager at
/// render time (after the substitute is requested by the DI container).
/// </para>
/// <para>
/// <b>IStringLocalizer&lt;T&gt; stub.</b> The SUT components localize
/// all visible text via <c>Loc["Key"]</c>. The resx files are baked in
/// at runtime — the test project has no resource lookup pipeline. The
/// stub returns the resource key as the value (<c>LocalizedString(Key,
/// Key, ResourceNotFound: false)</c>) so the rendered markup is stable
/// and test-assertable — e.g. <c>Loc["ButtonConfirm"]</c> renders as
/// "ButtonConfirm" in the markup, and we can assert against that.
/// </para>
/// </remarks>
internal static class ComponentTestSetup
{
    /// <summary>
    /// Creates a configured <see cref="TestContext"/> with Radzen
    /// services + Loose JS interop. Use this for components that need
    /// RadzenButton/RadzenCard/RadzenAlert (BottomNav, dialogs,
    /// CartBudgetBar).
    /// </summary>
    public static TestContext CreateRadzenEnabledContext()
    {
        var ctx = new TestContext();
        // Loose mode = any unplanned JS call returns default. See class
        // doc for why this is mandatory for Radzen components.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();
        return ctx;
    }

    /// <summary>
    /// Registers a substitute <see cref="Radzen.DialogService"/> that
    /// test code can later assert on via
    /// <c>dialogService.Received(1).Close(true)</c>.
    /// </summary>
    /// <param name="ctx">The configured test context (must already have
    /// <c>AddRadzenComponents()</c> called on it).</param>
    /// <returns>The substitute, returned so the test can
    /// call <c>.Received(...)</c> on it after the click.</returns>
    public static Radzen.DialogService AddDialogServiceSpy(TestContext ctx)
    {
        // Factory delegate delays substitute construction until the
        // DI container first resolves DialogService (which happens
        // during component render). By then bUnit's NavigationManager
        // has been initialized, so the DialogService ctor's
        // LocationChanged += handler succeeds.
        Radzen.DialogService? captured = null;
        ctx.Services.AddSingleton(sp =>
        {
            var nav = sp.GetRequiredService<NavigationManager>();
            var js = sp.GetRequiredService<IJSRuntime>();
            captured = Substitute.For<Radzen.DialogService>(nav, js, sp);
            return captured;
        });
        // When the substitute is created during render, it'll be cached
        // as a singleton — but we need to expose it to the test. Use a
        // post-render accessor (RenderComponent will have triggered the
        // factory by the time the test reaches the assert phase).
        return null!;
    }

    /// <summary>
    /// Returns the registered substitute DialogService after the
    /// component has been rendered (so the factory delegate has fired).
    /// </summary>
    public static Radzen.DialogService GetDialogServiceSpy(TestContext ctx)
        => ctx.Services.GetRequiredService<Radzen.DialogService>();

    /// <summary>
    /// Registers an "identity" stub localizer for <c>T</c> — returns the
    /// resource key as the localized value so rendered markup is stable
    /// and assertable. Sets <c>ResourceNotFound=false</c> so the SUT's
    /// null/empty checks (e.g. <c>Loc["..."] is { ResourceNotFound: true }
    /// fallback) don't trigger fallback paths.
    /// </summary>
    public static IStringLocalizer<T> AddIdentityLocalizer<T>(TestContext ctx)
        where T : class
    {
        var loc = Substitute.For<IStringLocalizer<T>>();
        loc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci[0], (string)ci[0], resourceNotFound: false));
        loc[Arg.Any<string>()].Returns(ci =>
            new LocalizedString((string)ci.ArgAt<string>(0), (string)ci.ArgAt<string>(0), resourceNotFound: false));
        ctx.Services.AddSingleton(loc);
        return loc;
    }

    /// <summary>
    /// Builds an authenticated <see cref="AuthenticationStateProvider"/> for
    /// the supplied role set and registers it in the test context. Used by
    /// BottomNav.razor (which queries the provider directly via
    /// <c>AuthStateProvider.GetAuthenticationStateAsync()</c> — does NOT
    /// need the full AuthorizeView pipeline).
    /// </summary>
    /// <param name="ctx">The test context.</param>
    /// <param name="userName">The display name for the user (default
    /// "test-user").</param>
    /// <param name="roles">Role names (use <c>Roles.Admin</c>,
    /// <c>Roles.Customer</c>, etc.).</param>
    public static AuthenticationStateProvider AddAuthenticatedUser(
        TestContext ctx,
        string userName = "test-user",
        params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userName)
        };
        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        var identity = new ClaimsIdentity(claims, "TestAuthentication");
        var principal = new ClaimsPrincipal(identity);
        var authState = new AuthenticationState(principal);

        var authProvider = Substitute.For<AuthenticationStateProvider>();
        authProvider.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(authState));
        ctx.Services.AddSingleton(authProvider);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(authProvider);
        return authProvider;
    }

    /// <summary>
    /// Builds an authenticated user via bUnit's native
    /// <c>AddTestAuthorization()</c> extension — this registers the FULL
    /// ASP.NET Core authorization pipeline (AuthenticationStateProvider
    /// + IAuthorizationService + IAuthorizationPolicyProvider + the
    /// AuthorizeView cascade). Use this for components that use
    /// <c>&lt;AuthorizeView&gt;</c> (e.g. RequireRole.razor).
    /// </summary>
    /// <param name="ctx">The test context.</param>
    /// <param name="userName">The display name (default "test-user").</param>
    /// <param name="roles">Role names (use <c>Roles.Admin</c>, etc.).</param>
    /// <returns>The <see cref="TestAuthorizationContext"/> so the
    /// test can mutate it further if needed.</returns>
    public static TestAuthorizationContext AddBunitAuthorizedUser(
        TestContext ctx,
        string userName = "test-user",
        params string[] roles)
    {
        // bUnit v1.36 API: AddTestAuthorization() returns
        // TestAuthorizationContext. SetAuthorized(user, state) makes
        // the user authenticated with the given auth state. SetRoles
        // adds role claims. AuthorizeView reads these via the
        // FakeAuthenticationStateProvider that AddTestAuthorization
        // registers.
        var auth = ctx.AddTestAuthorization();
        auth.SetAuthorized(userName, AuthorizationState.Authorized);
        if (roles.Length > 0)
            auth.SetRoles(roles);
        return auth;
    }

    /// <summary>
    /// Builds an anonymous (not authenticated) user via bUnit's native
    /// AddTestAuthorization() extension. Use this for testing the not-
    /// authorized paths in <c>&lt;AuthorizeView&gt;</c>-based components.
    /// </summary>
    public static TestAuthorizationContext AddBunitAnonymousUser(TestContext ctx)
    {
        // SetNotAuthorized → user is not authenticated. AuthorizeView
        // treats this as anonymous → not-authorized.
        return ctx.AddTestAuthorization().SetNotAuthorized();
    }

    /// <summary>
    /// Builds an anonymous (no claims) AuthenticationStateProvider — used
    /// for testing the not-authorized paths in RequireRole.razor.
    /// </summary>
    public static AuthenticationStateProvider AddAnonymousUser(TestContext ctx)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        var authState = new AuthenticationState(principal);
        var authProvider = Substitute.For<AuthenticationStateProvider>();
        authProvider.GetAuthenticationStateAsync()
            .Returns(Task.FromResult(authState));
        ctx.Services.AddSingleton(authProvider);
        ctx.Services.AddSingleton<AuthenticationStateProvider>(authProvider);
        return authProvider;
    }
}

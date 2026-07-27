using Microsoft.AspNetCore.Localization;
using Radzen;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.DependencyInjection;
using TakOne.Infrastructure.DependencyInjection;
using TakOne.Infrastructure.Identity;
using TakOne.WebUI.Components;
using TakOne.WebUI.Hubs;
using TakOne.WebUI.Services;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// ==================================================================================================================================
//                                                          SERVICES
// ==================================================================================================================================

// --- Application + Infrastructure DI composition roots ---
// All framework wiring (Identity, EF Core, Wolverine outbox, repositories,
// Identity options, cookie config) lives in AddTakOneInfrastructure per
// Concern F in the roadmap. The WebUI's Program.cs stays clean.
// builder.Environment is passed so Infrastructure can branch on
// IsDevelopment() (e.g. for detailed exception pages, dev-only
// EF Core sensitive-data-logging, etc.).
builder.Services.AddTakOneApplication(builder.Configuration);
builder.Services.AddTakOneInfrastructure(builder.Configuration, builder.Environment);
// --- Wolverine host ---
// Per roadmap concern: Wolverine options are already configured inside
// AddTakOneApplication (handler discovery, FluentValidation, middleware
// pipeline) and AddTakOneInfrastructure (SQL Server message store, EF Core
// transactional outbox, domain-event scraper).
//
// CRITICAL: the lambda passed here MUST re-include the Application assembly
// for handler discovery. Wolverine's default behavior is to scan ONLY the
// entry-point assembly (TakOne.WebUI), but our handlers live in
// TakOne.Application — a REFERENCED assembly, which is silently dropped
// without this explicit IncludeAssembly call.
//
// AddTakOneApplication already calls
//   opts.Discovery.IncludeAssembly(typeof(ServiceCollectionExtensions).Assembly);
// via services.Configure<WolverineOptions>(...) — and that DOES get applied
// when UseWolverine() pulls options from DI. But experience (and the
// "Wolverine found no handlers" warning in the startup log) shows this is
// fragile across Wolverine versions. Re-stating it here is cheap and
// eliminates any ambiguity: Wolverine WILL scan TakOne.Application on every
// startup, period.
builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(TakOne.Application.DependencyInjection.ServiceCollectionExtensions).Assembly);
});

// --- Blazor Server + Radzen ---
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

// --- SignalR (used by NotificationHub + Blazor circuit) ---
builder.Services.AddSignalR();

// --- Current user service (Blazor Server specific) ---
// ICurrentUserService is defined in Application (sync interface for now).
// The Blazor implementation reads from HttpContextAccessor at the start of
// the circuit. The async refactor (Concern B in roadmap) is deferred — see
// worklog Phase 0 entry for rationale.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, BlazorCurrentUserService>();

// --- WebUI-only services ---
builder.Services.AddScoped<ToastService>();

// --- Localization (Persian default + English secondary, see roadmap Section 5) ---
//
// AddLocalization() registers IStringLocalizer<T> and IStringLocalizerFactory
// as scoped services. WITHOUT this call, [Inject] IStringLocalizer<MyComponent>
// in any .razor file throws at render time:
//   "Cannot provide a value for property 'Localizer' on type '...'.
//    There is no registered service of type
//    'IStringLocalizer`1[...]'."
//
// This is a separate registration from BOTH:
//   - Configure<RequestLocalizationOptions>(...) below — only tunes the
//     options object the middleware reads (default culture, supported list).
//   - app.UseRequestLocalization() in the pipeline — only sets
//     CultureInfo.CurrentCulture / CurrentUICulture per request from
//     the cookie/accept-header.
// Neither of those two alone registers IStringLocalizer<T> services.
//
// Type-to-resource mapping is by convention: IStringLocalizer<LoginLayout>
// looks for Resources/Components/Layout/LoginLayout.{culture}.resx (and the
// same path co-located next to the .razor file). Our .resx files are
// co-located (e.g. Components/Layout/LoginLayout.fa-IR.resx), which is the
// pattern ASP.NET Core's default ResourceManagerStringLocalizerFactory picks
// up automatically — no custom IStringLocalizerFactory needed.
builder.Services.AddLocalization();

var supportedCultures = builder.Configuration
    .GetSection("TakOne:Localization:SupportedCultures")
    .Get<string[]>() ?? new[] { "fa-IR", "en-US" };
var defaultCulture = builder.Configuration
    .GetSection("TakOne:Localization:DefaultCulture")
    .Get<string>() ?? "fa-IR";

// ─── RequestLocalizationOptions with EXPLICIT provider chain ──────────────
// The default RequestLocalizationOptions uses the built-in provider chain:
//   1. QueryStringRequestCultureProvider  (URL ?culture=fa-IR)
//   2. CookieRequestCultureProvider       (cookie .AspNetCore.Culture=c=fa-IR|uic=fa-IR)
//   3. AcceptLanguageHeaderRequestCultureProvider (browser Accept-Language header)
//
// PROBLEM: MainLayout.razor's OnCultureChanged writes a cookie named
// `takone_culture` (NOT the default `.AspNetCore.Culture`) and uses the bare
// format `fa-IR` (NOT `c=fa-IR|uic=fa-IR`). The default cookie provider can't
// read it, so the user's language choice doesn't persist across page loads.
//
// FIX: clear the default provider list and rebuild it explicitly with a
// CookieRequestCultureProvider whose CookieName is `takone_culture`. We use
// MakeCookieValue to encode the culture in the format ASP.NET Core expects
// (which CookieRequestCultureProvider.ParseCookieValue will then accept).
//
// Order is intentional:
//   1. QueryString — needed for /Account/Login links (?culture=en-US) which
//      don't go through MainLayout's language switcher.
//   2. Cookie — persists the user's choice across page loads.
//   3. Accept-Language — falls back to the browser's preference for first
//      visit (before the user has picked anything).
var localizationOptions = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture(defaultCulture),
    ApplyCurrentCultureToResponseHeaders = true
};
localizationOptions.AddSupportedCultures(supportedCultures);
localizationOptions.AddSupportedUICultures(supportedCultures);

// Clear default providers and rebuild with the custom cookie name.
localizationOptions.RequestCultureProviders.Clear();
localizationOptions.RequestCultureProviders.Insert(0, new QueryStringRequestCultureProvider());
localizationOptions.RequestCultureProviders.Insert(1, new CookieRequestCultureProvider
{
    CookieName = "takone_culture"
});
localizationOptions.RequestCultureProviders.Insert(2, new AcceptLanguageHeaderRequestCultureProvider());

builder.Services.Configure<RequestLocalizationOptions>(opts =>
{
    opts.DefaultRequestCulture = new RequestCulture(defaultCulture);
    opts.AddSupportedCultures(supportedCultures);
    opts.AddSupportedUICultures(supportedCultures);
    opts.ApplyCurrentCultureToResponseHeaders = true;

    // Mirror the same explicit provider chain here so the options that
    // UseRequestLocalization() reads from DI match the ones we set above.
    opts.RequestCultureProviders.Clear();
    opts.RequestCultureProviders.Add(new QueryStringRequestCultureProvider());
    opts.RequestCultureProviders.Add(new CookieRequestCultureProvider
    {
        CookieName = "takone_culture"
    });
    opts.RequestCultureProviders.Add(new AcceptLanguageHeaderRequestCultureProvider());
});

var app = builder.Build();

// ==================================================================================================================================
//                                                          MIDDLEWARE PIPELINE
// ==================================================================================================================================

// Request localization must be early in the pipeline so culture is set
// before any handler runs.
app.UseRequestLocalization();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// NOTE: the method is `UseStatusCodePagesWithReExecute` (capital R, capital E)
// — C# is case-sensitive. There is no `createScopeForStatusCodePages`
// parameter on this overload (that parameter exists on
// UseExceptionHandler, not on the status-code pages APIs).
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<NotificationHub>("/notificationHub");

// ==================================================================================================================================
//                                                          STARTUP-TIME SEEDING
// ==================================================================================================================================

// Role seeding — ensures all 5 TakOne roles (Admin, Manager, Employee,
// Customer, ReadOnly) exist in AspNetRoles before the app starts accepting
// requests. Without this, the first user-creation attempt via
// IUserAccountService.CreateIdentityAccountAsync would fail with
// "Role X does not exist."
//
// This call is awaited BEFORE app.RunAsync() so there's no race window
// where a request arrives before roles are seeded. See RoleSeeder.cs for
// the design rationale (static method vs IHostedService vs EF HasData).
await RoleSeeder.EnsureRolesCreatedAsync(app.Services);

// Default admin seeding — creates a single bootstrap Admin user IF (and
// only if) no user currently holds the Admin role. Breaks the
// chicken-and-egg: you can't log in to create the first admin because the
// user-management page is locked to Admin/Manager. After this runs once,
// you have a known login to bootstrap the rest of the system from.
//
// Default credentials are logged at WARNING level on first creation and
// are documented in DefaultAdminSeeder.cs. CHANGE THE PASSWORD after
// first login.
//
// Idempotent: GetUsersInRoleAsync("Admin") non-empty → no-op. Safe to
// leave wired in for every startup.
await DefaultAdminSeeder.EnsureDefaultAdminAsync(app.Services);

// ==================================================================================================================================
//                                                          RUN
// ==================================================================================================================================

// Use RunAsync (not Run) so the await chain stays consistent with the
// async RoleSeeder call above. Top-level statements support async natively
// — the compiler generates an async Main entry point under the hood.
await app.RunAsync();
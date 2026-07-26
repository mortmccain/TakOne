using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Radzen;
using TakOne.Application.Common.Authorization;
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
builder.Services.AddTakOneApplication(builder.Configuration);
builder.Services.AddTakOneInfrastructure(builder.Configuration);

// --- Wolverine host (empty lambda) ---
// Per roadmap concern: Wolverine options are already fully configured inside
// AddTakOneInfrastructure (handler discovery, FluentValidation, middleware
// pipeline, SQL Server message store, EF Core transactional outbox, domain
// event scraper). The WebUI only needs to start the host with an empty
// lambda so Wolverine registers itself as the message bus.
//
// NOTE: a previous version of this block set `opts.ServiceLocationPolicy`
// and `opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic`. Both have
// been REMOVED because the `ServiceLocationPolicy` enum isn't resolvable
// in the Wolverine 6.20.0 / JasperFx.CodeGeneration API we're targeting
// (the API surface has shifted across versions, and rather than chase it
// we rely on Wolverine's defaults — which already allow service location
// when a handler needs it). If a runtime error eventually demands one of
// these settings, we'll add it back with the exact API for whatever
// version is pinned at that time.
builder.Host.UseWolverine();

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

var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture(defaultCulture)
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

builder.Services.Configure<RequestLocalizationOptions>(opts =>
{
    opts.SetDefaultCulture(defaultCulture);
    opts.AddSupportedCultures(supportedCultures);
    opts.AddSupportedUICultures(supportedCultures);
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
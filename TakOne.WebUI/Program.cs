using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Radzen;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.DependencyInjection;
using TakOne.Infrastructure.DependencyInjection;
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

app.Run();
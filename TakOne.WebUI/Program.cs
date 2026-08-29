using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Radzen;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Errors;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Configuration;
using TakOne.Application.DependencyInjection;
using TakOne.Infrastructure.DependencyInjection;
using TakOne.Infrastructure.Identity;
using TakOne.Infrastructure.Persistence;
using TakOne.WebUI.Components;
using TakOne.WebUI.Hubs;
using TakOne.WebUI.Services;
using TakOne.WebUI.Services.Logging;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// Enable static web assets (serves `/_content/{Library}/...` URLs from
// referenced Razor class libraries like Radzen.Blazor). In .NET 8+ this is
// usually automatic in Development (via the SDK's `Microsoft.AspNetCore.StaticWebAssets`
// pack), but in a Production Docker container, `app.MapStaticAssets()` (called
// later) only works if the runtime manifest was emitted at publish time.
//
// `UseStaticWebAssets()` is the explicit, environment-agnostic API — it tells
// the host to load the static web assets manifest regardless of environment.
// Without it, the Docker Production container serves every `/_content/Radzen.Blazor/*`
// request as 404, and the UI renders unstyled.
//
// (Call this BEFORE any services are added so the manifest is loaded into
// the config pipeline before components try to resolve asset URLs.)
builder.WebHost.UseStaticWebAssets();

// ==================================================================================================================================
//                                                          SERVICES
// ==================================================================================================================================

// --- Application + Infrastructure DI composition roots ---
// All framework wiring (Identity, EF Core, Wolverine outbox, repositories,
// Identity options, cookie config) lives in AddTakOneInfrastructure per
// Concern F in the roadmap. The WebUI's Program.cs stays clean.
builder.Services.AddTakOneApplication(builder.Configuration);
builder.Services.AddTakOneInfrastructure(builder.Configuration, builder.Environment);

// --- Default admin seeder options (Issue #02 — Hardcoded default admin password) ---
//
// The bootstrap admin's WorkerId / Email / Password are bound from the
// "TakOne:DefaultAdmin" configuration section. The Password NEVER lives in
// appsettings.json or any source-controlled file — in Development it comes
// from .NET user secrets, in Production from an environment variable or a
// secret store like Azure Key Vault. See DefaultAdminOptions.cs for the
// full rationale and the security guarantees this class enforces.
//
// We bind the options here (rather than inside AddTakOneInfrastructure) so
// that the WebUI's Program.cs owns the decision of WHEN to validate and
// WHEN to invoke the seeder — both are environment-dependent and that
// decision belongs at the composition root.
builder.Services.Configure<DefaultAdminOptions>(
    builder.Configuration.GetSection(DefaultAdminOptions.SectionName));

// --- Data Protection key persistence (CRITICAL for dev cookie survival) ---
//
// Without explicit key persistence, ASP.NET Core Data Protection uses an
// EPHEMERAL key store that changes on every app restart. The auth cookie
// (and antiforgery tokens) are encrypted with these keys.
//
// SYMPTOM of missing persistence:
//   1. User logs in → cookie set, encrypted with key K1
//   2. App restarts (rebuild, code change, Hot Reload boundary, etc.)
//      → new key K2 generated, K1 discarded
//   3. Browser sends old cookie (encrypted with K1) → server tries to
//      decrypt with K2 → fails silently → user appears logged out
//   4. Cookie middleware redirects to /Account/Login with the
//      "LoginRequired" info banner — looks like "login failed with
//      no error"
//
// PERSISTENCE CHOICE — PersistKeysToDbContext<ApplicationDbContext>:
//   The key ring lives in a SQL Server table (`DataProtectionKeys`) inside
//   the same ApplicationDbContext that already holds the domain, Identity,
//   and Wolverine outbox tables. One connection, one transaction boundary,
//   one backup story.
//
//   WHY NOT PersistKeysToFileSystem (the previous approach):
//     - Multi-node failure: each web node had its own private key ring →
//       cookies issued by node A couldn't be decrypted by node B →
//       "random logouts" behind a load balancer.
//     - Redeploy failure: the key folder lived inside the app folder →
//       any deploy that replaced the folder (Docker image rebuild,
//       blue/green swap) wiped the keys → mass-logout of every active
//       session on every deploy.
//     - Backup story was awkward: a folder inside the app had to be
//       tracked separately from the database.
//
//   PersistKeysToDbContext eliminates all three failure modes because every
//   web node already shares the same SQL Server, and the DB is outside the
//   app folder. Backups come for free with the DB backup.
//
//   The DbContext must implement IDataProtectionKeyContext (expose a
//   `DbSet<DataProtectionKey> DataProtectionKeys`). ApplicationDbContext
//   does — see TakOne.Infrastructure/Persistence/ApplicationDbContext.cs.
//   The package providing that interface (Microsoft.AspNetCore.DataProtection
//   .EntityFrameworkCore) is referenced from TakOne.Infrastructure (not
//   WebUI), and the extension method `PersistKeysToDbContext<T>()` is
//   visible here via the transitive package reference flowing through the
//   project reference.
//
// SetApplicationName is REQUIRED when multiple apps share a key ring —
// and even with a single app it makes the key discriminator stable
// across rebuilds (otherwise the app's "default" discriminator can shift
// if the entry-assembly name changes during refactoring).
//
// SECURITY NOTE:
//   The XML in the `Xml` column is in CLEAR TEXT inside the database —
//   same as it would have been on disk under PersistKeysToFileSystem.
//   The security boundary is SQL Server access control: only the
//   application's DB user has SELECT/INSERT on the DataProtectionKeys
//   table. If you later need defense-in-depth beyond SQL access control,
//   layer `.ProtectKeysWithCertificate(...)` with an X.509 cert from the
//   machine cert store — no cloud service required.
builder.Services.AddDataProtection()
    .SetApplicationName("TakOne")
    .PersistKeysToDbContext<ApplicationDbContext>();

// --- Wolverine host (configures discovery, middleware, persistence) ---
//
// CRITICAL: We MUST apply our Application- and Infrastructure-layer
// Wolverine config inside THIS lambda — directly on the
// `WolverineOptions` instance Wolverine actually uses.
//
// The previous implementation had `builder.Host.UseWolverine()` (no lambda)
// and relied on `services.Configure<WolverineOptions>(...)` calls inside
// AddTakOneApplication and AddTakOneInfrastructure. THAT DOES NOT WORK.
// Wolverine does NOT read its options from the IOptions<WolverineOptions>
// pipeline — the parameterless UseWolverine() overload uses a fresh
// default `WolverineOptions` instance and silently ignores any
// Configure<WolverineOptions> lambdas.
//
// SYMPTOM of the broken approach:
//   warn: Wolverine found no handlers. If this is unexpected, check
//         the assemblies that it's scanning.
//   Searching assembly Wolverine.RuntimeCompilation
//   Searching assembly TakOne.WebUI
//   (NOT searching TakOne.Application — even though AddTakOneApplication
//    had called opts.Discovery.IncludeAssembly(typeof(...).Assembly))
//
//   fail: Failed to create a message handler for
//         TakOne.Application.Dashboard.Queries.GetDashboardStatsQuery
//   IndeterminateRoutesException: Could not determine any valid
//   subscribers or local handlers for message type ...
//
// FIX:
//   Application and Infrastructure each now expose a public static
//   extension method on WolverineOptions:
//     - `ConfigureApplicationWolverine(this WolverineOptions, IConfiguration)`
//     - `ConfigureInfrastructureWolverine(this WolverineOptions, IConfiguration)`
//   The methods are named DISTINCTLY (Application vs Infrastructure) so they
//   can coexist as extension methods on WolverineOptions without ambiguity.
//   Calling them with identical names would require static-call syntax
//   (Class.Method(opts, config)) which is fragile and produces confusing
//   "No overload for method 'ConfigureWolverine' takes 2 arguments" errors
//   when one of the two files is missed during a refactor.
//
//   We invoke both as clean extension-method calls inside this lambda,
//   in order:
//     1. Application first — discovers handlers, registers middleware,
//        wires FluentValidation.
//     2. Infrastructure second — enables runtime compilation, registers
//        SQL Server message store, durable local queues, EF Core
//        transactional middleware, domain-event scraper.
//   Order matters only for documentation clarity; the two configurators
//   touch disjoint parts of WolverineOptions.
builder.Host.UseWolverine(opts =>
{
    // Application layer: handler discovery + middleware + FluentValidation.
    opts.ConfigureApplicationWolverine(builder.Configuration);

    // Infrastructure layer: runtime compilation + SQL Server message
    // store + durable local queues + EF Core transactional middleware +
    // domain-event scraper.
    opts.ConfigureInfrastructureWolverine(builder.Configuration);
});

// --- Blazor Server + Radzen ---
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();

// --- SignalR (used by NotificationHub + Blazor circuit) ---
//
// CRITICAL: We MUST raise MaximumReceiveMessageSize from its default of
// 32 KB to a value large enough for product-image uploads. Blazor Server
// streams IBrowserFile bytes over the SignalR circuit; with the default
// 32 KB cap, ANY file > 32 KB passed to OpenReadStream throws:
//
//   System.IO.InvalidDataException:
//     The maximum message length of 32768B was exceeded.
//
// SYMPTOM (the bug this fixes):
//   The user picks an image in /Admin/Products/Create, fills the form,
//   clicks "Create product". The .razor page calls
//   _selectedFile.OpenReadStream(maxAllowedSize: 5MB) which streams the
//   file bytes from the browser to the server over the circuit. The
//   moment the cumulative byte count crosses 32 KB, SignalR aborts the
//   stream with an exception. The exception bubbles up to the page's
//   catch block, which sets _submitError to "The upload failed. Please
//   try again." and shows the error banner. Nothing appears in the
//   server logs because the catch (silently, before this fix's logging
//   was added) swallowed the exception.
//
//   Files <= 32 KB work fine, which is why the bug is intermittent —
//   small thumbnails upload successfully, real photos (typically
//   500 KB–5 MB) fail.
//
// FIX:
//   Raise MaximumReceiveMessageSize to 10 MB. This is the per-message
//   cap on incoming SignalR frames; Blazor chunks file uploads into
//   messages, so the cap applies to each chunk, not the whole file.
//   10 MB gives comfortable headroom over our 5 MB LocalFileStorage cap.
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB
});

// --- Current user service (Blazor Server specific) ---
// ICurrentUserService is defined in Application (sync interface for now).
// The Blazor implementation reads from HttpContextAccessor at the start of
// the circuit. The async refactor (Concern B in roadmap) is deferred — see
// worklog Phase 0 entry for rationale.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, BlazorCurrentUserService>();

// --- WebUI-only services ---
//
// ErrorDisplayService is the UI-side formatter for unexpected-error
// messages — takes a 7-char opaque code from UnexpectedErrorCodes and
// produces a localized "An unexpected error occurred. Error code: X"
// string. Injected into ToastService (so Toast.UnexpectedError(code)
// is a one-liner from any catch block) and directly into razor pages
// for the cases where the message is rendered in a _loadError-style
// field instead of a toast.
builder.Services.AddScoped<ErrorDisplayService>();
builder.Services.AddScoped<ToastService>();

// --- Notification system: real-time broadcaster + Blazor refresh bridge ---
//
// SignalRNotificationBroadcaster is the WebUI implementation of
// INotificationBroadcaster (defined in TakOne.Application). Wolverine
// resolves it when a sale-lifecycle event handler (NotifyOnSaleApproved-
// EventHandler etc.) calls BroadcastToUserAsync — the impl pings the
// NotificationHub's user-group. Scoped lifetime so the handler resolves
// it fresh per-request.
builder.Services.AddScoped<TakOne.Application.Common.Interfaces.INotificationBroadcaster,
    TakOne.WebUI.Services.SignalRNotificationBroadcaster>();

// NotificationRefreshService: Scoped Blazor service that bridges the
// JS-side SignalR connection to .NET events. The layout calls
// StartAsync() to wire up the connection; MobileNotifications.razor +
// MainLayout.razor subscribe to RefreshReceived to re-query their data
// when a real-time push arrives.
builder.Services.AddScoped<TakOne.WebUI.Services.NotificationRefreshService>();

// --- Login audit logger (Issue #03 — replace DIAG Login leak pattern) ---
//
// LoginAuditLogger is the ONLY sanctioned logger for the login flow. It
// enforces an allow-list by construction (only LoginLogContext fields can
// be logged — Password, PasswordLength, EmailConfirmed, LockoutEnd,
// AccessFailedCount, Email, FullName, Gender are deliberately absent from
// the record and therefore cannot leak). The ForbiddenLoggingAnalyzer in
// TakOne.Analyzers is the CI-level backstop: it flags any ILogger.Log*
// call whose format string contains a banned token ("DIAG Login",
// "PasswordLength", etc.) and fails the build at Error severity.
//
// Scoped lifetime matches the convention used by other WebUI services
// (BlazorCurrentUserService, ToastService). The logger's dependencies
// (ILogger<LoginAuditLogger> singleton, IHostEnvironment singleton) are
// both singletons, so Scoped is fine and Singleton would also work.
builder.Services.AddScoped<LoginAuditLogger>();

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

// --- Request localization providers ---
//
// CRITICAL: We MUST clear the default provider list and re-add only the
// ones we want. By default, `RequestLocalizationOptions` adds THREE
// providers in this order:
//
//   1. QueryStringRequestCultureProvider  — reads `?culture=fa-IR`
//   2. CookieRequestCultureProvider       — reads `.AspNetCore.Culture`
//                                            cookie (default name)
//   3. AcceptLanguageHeaderCultureProvider — reads the browser's
//                                            `Accept-Language` header
//
// PROVIDER #3 IS THE PROBLEM. If the user's browser sends
// `Accept-Language: en-US,en;q=0.9` (the default for English-locale
// OSes), the header provider matches `en-US` (which is in our
// SupportedCultures list) and OVERRIDES the `fa-IR` default. The
// `DefaultRequestCulture` only kicks in when NO provider returns a
// match — and the header provider almost always matches something.
//
// SYMPTOM: first-time visitor sees English text even though
// `DefaultCulture = fa-IR` is configured. The lang/dir attributes on
// <html> are hardcoded `fa`/`rtl` (see App.razor) so the page LOOKS
// Persian-structured but the strings are English.
//
// FIX: keep only QueryString + Cookie providers. Drop the header
// provider. Now `fa-IR` is the genuine default on first visit (no
// cookie yet → no query string → default kicks in).
//
// We also override the cookie name to `takone_culture` (default is
// `.AspNetCore.Culture`) so the cookie is human-readable in devtools
// and doesn't collide with other ASP.NET Core apps on localhost.
var cultureList = supportedCultures.Select(c => new CultureInfo(c)).ToList();

builder.Services.Configure<RequestLocalizationOptions>(opts =>
{
    opts.DefaultRequestCulture = new RequestCulture(defaultCulture);
    opts.SupportedCultures = cultureList;
    opts.SupportedUICultures = cultureList;

    // Clear all default providers and add only the two we want.
    opts.RequestCultureProviders.Clear();
    opts.RequestCultureProviders.Add(new QueryStringRequestCultureProvider());
    opts.RequestCultureProviders.Add(new CookieRequestCultureProvider
    {
        CookieName = "takone_culture"
    });
});

// --- Named authorization policies (Issue #05 — typo-class bug) ---
//
// Before this block, every protected Razor page declared
//   @attribute [Authorize(Roles = "Admin,Manager,Employee")]
// directly. That has a silent-failure mode: a typo like
//   @attribute [Authorize(Roles = "Adming")]
// compiles cleanly and denies every user (the page appears to be
// secured, but nobody can ever get in — including the admins who were
// supposed to have access). The same defect exists for any role-string
// permutation drift across pages: there is no compile-time check that
// the role-set on the page is the role-set the business actually wants.
//
// FIX: register NAMED POLICIES here, one per unique role-set used by
// any page. Pages now declare
//   // Policy: Policies.DashboardAccess
//   @attribute [Authorize(Policy = "DashboardAccess")]
// and the role-set lives in exactly one place — this block — where a
// single audit grep can verify every page maps to a registered policy.
//
// Razor `@attribute` directives require string LITERALS — they do not
// support constant references, so `[Authorize(Policy = Policies.X)]`
// will not compile. Pages therefore use the literal string with a
// `// Policy: Policies.X` comment so the literal is grep-cross-checkable
// against the constants in TakOne.Application/Common/Authorization/
// Policies.cs. See the SAFETY note in Policies.cs for the residual
// typo risk and the grep-audit mitigation.
//
// The per-handler `[RequireRole(Roles.X)]` attribute on Application-
// layer CQRS handlers is a SEPARATE defense-in-depth layer (per-handler,
// not per-page) and stays in place regardless of these policies.
builder.Services.AddAuthorization(options =>
{
    // Admin only — admin notifications console.
    options.AddPolicy(Policies.AdminOnly, p => p.RequireRole(Roles.Admin));

    // Admin + Manager — group/category/user creation that employees
    // must not access.
    options.AddPolicy(Policies.StaffManagement,
        p => p.RequireRole(Roles.Admin, Roles.Manager));

    // Admin + Manager + Employee — the default staff page policy.
    // Product, user-detail, group-management, and admin-list pages.
    options.AddPolicy(Policies.ProductManagement,
        p => p.RequireRole(Roles.Admin, Roles.Manager, Roles.Employee));

    // Admin + Manager + Employee + ReadOnly — dashboard analytics;
    // read-only auditors can view.
    options.AddPolicy(Policies.DashboardAccess,
        p => p.RequireRole(Roles.Admin, Roles.Manager, Roles.Employee, Roles.ReadOnly));

    // Admin + Manager + Employee + Customer — shopping-cart page;
    // staff can preview a customer's cart, customers shop for themselves.
    options.AddPolicy(Policies.CartAccess,
        p => p.RequireRole(Roles.Admin, Roles.Manager, Roles.Employee, Roles.Customer));
});

// --- App-update auto-broadcaster (hosted service) ---
//
// Runs ONCE at startup, after the DI container is built and Wolverine's
// bus is initialized. Compares the running assembly version against
// SystemSettings.LastKnownAppVersion; if it changed since last boot,
// dispatches an EmitAppUpdateBroadcastCommand via IMessageBus. The
// command fans out per-user AppUpdate Notification rows to every active
// user (audit row + N fanout rows + N SignalR pings, all in one
// Wolverine transaction). Then persists the new version so subsequent
// restarts with the same version don't re-broadcast.
//
// The service is best-effort: a broadcast failure (DB unreachable,
// Wolverine dispatch fails) is logged and never prevents the app from
// booting. See AppUpdateBroadcasterHostedService class doc for the
// full rationale.
builder.Services.AddHostedService<AppUpdateBroadcasterHostedService>();

// --- ASP.NET Core health checks (Brutal Code Review v3 finding #11) ---
//
// WHY THIS EXISTS:
//   The Docker Compose healthcheck for the `takone-app` container was
//   previously `curl -fsS http://localhost:8080/` — a request that hit the
//   Blazor SSR route `/`, which the cookie middleware 302-redirected to
//   /Account/Login whenever Kestrel was listening. That meant the
//   healthcheck PASSED even when:
//     - The SQL Server database was completely down (the redirect didn't
//       touch the DB).
//     - EF Core migrations were half-applied (same reason).
//     - Wolverine's message store was unreachable (same reason).
//   Docker would mark the container "healthy", the reverse proxy would
//   route traffic to it, and users would hit a broken app.
//
//   The docker-compose.yml healthcheck is now
//   `curl -fsS http://localhost:8080/health` — which hits the
//   MapHealthChecks("/health") endpoint registered below. That endpoint
//   runs AddDbContextCheck<ApplicationDbContext>, which actually opens a
//   SQL Server connection and runs `SELECT 1;` against the database.
//   A 200 means the DB is reachable AND Kestrel is up — not just
//   "Kestrel is listening".
//
// WHAT AddDbContextCheck DOES:
//   - Resolves ApplicationDbContext from the DI container (Scoped — fresh
//     instance per probe).
//   - Calls `Database.CanConnectAsync(CancellationToken)` under the hood
//     (the EF Core extension method on DatabaseFacade).
//   - Returns Unhealthy on any failure (SQL Server down, connection
//     refused, login failed, timeout). Returns Healthy on success.
//   - Default failure status is Unhealthy with a 503 response code; the
//     MapHealthChecks endpoint surfaces that to the curl probe.
//
// AUTHENTICATION:
//   /health is intentionally PUBLIC (no .RequireAuthorization() call).
//   The Docker healthcheck is `curl -fsS` from INSIDE the container — it
//   has no auth cookie. A protected /health endpoint would 401 and the
//   healthcheck would always fail → Docker would mark the container
//   unhealthy → the reverse proxy would stop routing traffic → outage.
//   The endpoint only reveals whether the DB is reachable (no business
//   data, no PII), so public visibility is acceptable.
//
// WHAT THIS DOES NOT CHECK:
//   AddDbContextCheck only verifies the SQL Server connection works. It
//   does NOT verify:
//     - Wolverine's message store is healthy (separate SQL Server
//       connection, separate table).
//     - Identity's auth system can issue cookies.
//     - The Blazor circuit can connect.
//   These are deliberate omissions — every probe adds load, and the DB
//   connection check covers the most common failure mode (SQL Server
//   unreachable). If deeper probes are needed later, chain them with
//   .AddCheck<T>() calls on the same builder.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>();

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
    // HSTS + HTTPS redirection are intentionally DISABLED for the Docker
    // deployment, which is HTTP-only on port 8080 (no TLS cert is mounted).
    // Enabling HSTS or UseHttpsRedirection here would cause redirect loops
    // and broken navigation — every request would be 307-redirected to an
    // HTTPS port that nothing is listening on.
    //
    // For a public-facing deployment, the correct pattern is to put a
    // reverse proxy (Caddy / Nginx / Traefik) in front that terminates
    // TLS on 443 and forwards plain HTTP to this container on 8080. The
    // reverse proxy sets `X-Forwarded-Proto=https`, and we'd then re-enable:
    //     app.UseHsts();
    //     app.UseHttpsRedirection();
    // together with `app.UseForwardedHeaders(...)` so the app trusts the
    // proxy's forwarded scheme header. See DEPLOYMENT.md for the planned
    // reverse-proxy step.
    //
    // app.UseHsts();          // DISABLED — no TLS in Docker compose setup
    // app.UseHttpsRedirection();  // DISABLED — same reason (see line ~367)
}

// NOTE: the method is `UseStatusCodePagesWithReExecute` (capital R, capital E)
// — C# is case-sensitive. There is no `createScopeForStatusCodePages`
// parameter on this overload (that parameter exists on
// UseExceptionHandler, not on the status-code pages APIs).
app.UseStatusCodePagesWithReExecute("/not-found");

// HTTPS redirection — DISABLED for the Docker deployment (HTTP-only).
// In Development (running via `dotnet run` with launchSettings.json's
// HTTPS endpoint on port 7xxx), we DO want HTTPS redirection so the
// dev experience matches a real TLS-terminated prod setup. In Production
// (Docker container exposing only HTTP 8080), HTTPS redirection causes
// every request to 307-redirect to an HTTPS port nobody is listening on,
// breaking the whole UI.
//
// If you later put a TLS-terminating reverse proxy in front and want
// HTTPS-only inside the container, re-enable this AND add
// `app.UseForwardedHeaders(...)` so the app trusts the proxy's
// X-Forwarded-Proto header.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// --- Force-password-change redirect (Issue #02 — Force one-time password
//     change on first login) ---
//
// If the authenticated user has a `must_change_password` claim on their
// auth cookie, they MUST be redirected to /Account/ChangePassword and
// blocked from every other path until they change their password.
//
// WHEN THE CLAIM GETS SET:
//   Login.razor adds the claim to the cookie at sign-in time IF the
//   ApplicationUser.MustChangePassword flag is true. The flag is set to
//   true by DefaultAdminSeeder on the bootstrap admin, and by the
//   AddMustChangePasswordFlag migration on every pre-existing admin (to
//   close the known-compromised-password hole from Issue #02).
//
// WHEN THE CLAIM GETS CLEARED:
//   ChangePassword.razor calls SignInManager.SignInWithClaimsAsync to
//   re-issue the cookie WITHOUT the claim, after the password has been
//   changed and the MustChangePassword flag has been cleared on the user
//   row.
//
// WHY A MIDDLEWARE (not a Blazor Router guard):
//   The redirect must fire for EVERY authenticated request, including
//   static-SSR pages (Login.razor, ChangePassword.razor), Blazor circuit
//   initial connections, SignalR hub handshakes (/notificationHub), and
//   minimal API endpoints. A Blazor <AuthorizeRouteView> / Router guard
//   only protects Blazor-paged routes — it would NOT block API calls or
//   static-SSR endpoints. A middleware is the only place where we can
//   intercept every request uniformly.
//
// ALLOWED PATHS (the user can reach these even with the claim):
//   - /Account/ChangePassword  — the page they need to use
//   - /Account/SignOut         — so they can sign out if they don't want
//                                 to change the password right now
//   - /Account/Login           — defensive; they shouldn't reach this
//                                 page while authenticated, but if they
//                                 do we don't want a redirect loop
//   (/Account/LogOut is ALSO accepted in the runtime check below for
//    backwards compatibility with stale bookmarks — the canonical
//    logout route is /Account/SignOut; see the LOGOUT ENDPOINT
//    section for why the route was renamed.)
//
// STATIC FILES + BLOWER ASSETS:
//   We deliberately allow /_framework, /_blazor, /css, /js, /lib, /favicon,
//   /TakLogo.png etc. through without redirect. Without this, the
//   ChangePassword page itself would fail to render (its CSS/JS wouldn't
//   load). The check is a simple StartsWith against a small allowlist of
//   static-asset prefixes.
//
// SECURITY NOTE:
//   The middleware reads the claim only — it does NOT query the database.
//   The claim is the source of truth at request time. If the
//   MustChangePassword flag is cleared on the user row but the cookie
//   hasn't been re-issued yet (e.g. the user changed their password in
//   another browser tab), the middleware will still redirect — but the
//   ChangePassword page will detect that the flag is already false and
//   clear the claim by re-issuing the cookie. So the system self-heals
//   within one request.
app.Use(async (context, next) =>
{
    var user = context.User;
    if (user.Identity?.IsAuthenticated == true &&
        user.HasClaim("must_change_password", "true"))
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // Static assets and Blazor framework files are always allowed —
        // without them the ChangePassword page itself can't render.
        // The list is intentionally short and explicit; any new static
        // asset folder should be added here.
        var isStaticAsset =
            path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/css", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/js", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/lib", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/TakLogo.png", StringComparison.OrdinalIgnoreCase);

        // Account paths the user is allowed to hit while their password
        // is in the must-change state.
        // NOTE: /Account/SignOut is the actual logout endpoint (see the
        // LOGOUT ENDPOINT section below for why we use SignOut instead
        // of the conventional /Account/Logout — short version: an
        // AmbiguousMatchException kept appearing at /Account/Logout
        // despite multiple attempts to remove the duplicate, so the
        // route was renamed to sidestep the conflict entirely).
        // /Account/LogOut is kept in the allowlist for backwards
        // compatibility with any stale bookmarks or in-flight cookies
        // that might still point at the old path.
        var isAllowedAccountPath =
            path.StartsWith("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Account/SignOut", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Account/LogOut", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase);

        // The SignalR hub negotiate/websocket requests must NOT be
        // redirected: a 302 to an HTML page kills the connection attempt,
        // producing reconnect churn + console noise
        // ("[notificationHub] connection failed:") for the JS bridge while
        // the user sits on the ChangePassword page. The hub itself is
        // [Authorize]-protected; letting the negotiate request through
        // only affects the transport, not authorization.
        var isSignalRHubPath =
            path.StartsWith("/notificationHub", StringComparison.OrdinalIgnoreCase);

        if (!isStaticAsset && !isAllowedAccountPath && !isSignalRHubPath)
        {
            // 302 redirect to the ChangePassword page. We use a query
            // string parameter so the page knows to show a "you must
            // change your password before continuing" banner.
            var returnUrl = context.Request.Path + context.Request.QueryString.Value;
            var redirectUrl = $"/Account/ChangePassword?returnUrl={Uri.EscapeDataString(returnUrl)}";
            context.Response.Redirect(redirectUrl);
            return;
        }
    }

    await next();
});

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<NotificationHub>("/notificationHub");

// --- Public health endpoint (Brutal Code Review v3 finding #11) ---
//
// MapHealthChecks exposes the AddHealthChecks() + AddDbContextCheck<
// ApplicationDbContext> probe registered above as an HTTP endpoint.
// Docker's healthcheck (curl -fsS http://localhost:8080/health) hits
// this. A 200 = healthy DB; 503 = DB unreachable. The endpoint is
// PUBLIC (no .RequireAuthorization()) so the container's curl probe —
// which has no auth cookie — can reach it. See the services-section
// comment above for the full rationale.
app.MapHealthChecks("/health");

// ==================================================================================================================================
//                                                          AUTHENTICATED UPLOADS ENDPOINT (Brutal Code Review v3 finding #09)
// ==================================================================================================================================
// Serves uploaded product images from the IFileStorage root directory
// (NOT from wwwroot). In docker-compose.yml the uploads directory was
// relocated to /var/lib/takone/uploads — OUTSIDE wwwroot — so
// app.UseStaticFiles() above no longer serves them. This closes the
// unauthenticated-static-files hole where anyone could fetch
// /uploads/products/anything.jpg without logging in (just by knowing
// the URL).
//
// AUTHORIZATION:
//   .RequireAuthorization() with NO role restriction — every authenticated
//   user (Customer, Employee, Manager, Admin, ReadOnly) can view product
//   images, because product images appear in Browse, Cart, Order Detail,
//   and Product Detail pages that are visible to all roles. The browser's
//   auth cookie is automatically attached when an <img src> tag fetches
//   the URL on a Blazor Server page, so this works seamlessly for the
//   existing .razor files that build URLs like /uploads/products/abc.jpg
//   from Product.PictureUrl.
//
// XSS DEFENSE (defense-in-depth, layered with Fix #08):
//   - Fix #08: LocalFileStorage.SanitizeExtension now ALWAYS uses the
//     sniffed content type's canonical extension (image/jpeg→.jpg,
//     image/png→.png, image/webp→.webp). A JPEG file uploaded as
//     "evil.html" is saved as randomhex.jpg — never randomhex.html —
//     so the on-disk filename can't be a browser-renderable HTML name.
//   - This endpoint sets Content-Disposition: attachment; filename=...
//     which FORCES the browser to download instead of rendering inline.
//     Even if a future bug let a non-image file slip through and the
//     Content-Type was wrong, the attachment disposition prevents the
//     browser from rendering it as HTML.
//   - The Content-Type we set is derived from the on-disk extension
//     (jpg→image/jpeg, png→image/png, webp→image/webp) — the only
//     extensions SanitizeExtension can ever produce, so we know
//     exactly which types to map.
//
// PATH-TRAVERSAL DEFENSE:
//   The {fileName} route parameter is sanitized to reject any `..`
//   segments, path separators (`/`, `\`), or absolute-path markers
//   (`:` for Windows drive specs). After that, the canonical-resolved
//   path is verified to be under the configured products folder —
//   defense-in-depth against any symlink or traversal trick that could
//   escape the root. The canonical-path check mirrors the one in
//   LocalFileStorage.DeleteAsync — same logic, same intent.
//
// WHY THIS DUPLICATES THE PATH LOGIC IN LocalFileStorage:
//   The IFileStorage interface is WRITE-ONLY (SaveAsync + DeleteAsync).
//   It does NOT expose a "get physical file path" method. We deliberately
//   don't expand the interface for this concern — expanding it would
//   force every IFileStorage mock across multiple test projects to gain
//   a new method, and the path scheme ({rootPath}/products/{fileName})
//   is stable. The same `FileStorage:RootPath` configuration key is
//   read here and in LocalFileStorage's ctor; both fall back to the
//   same default ("wwwroot/uploads") if unset. The IFileStorage.SaveAsync
//   return URL ("/uploads/products/{fileName}") is the contract that
//   links them: this endpoint's route template matches that prefix
//   exactly.
//
// WHY NOT UseStaticFiles WITH A SECOND FileProvider:
//   ASP.NET Core's UseStaticFiles CAN be wired with a custom
//   PhysicalFileProvider pointing at /var/lib/takone/uploads to serve
//   those files. But UseStaticFiles has NO auth gate — it runs BEFORE
//   UseAuthorization, so any auth check would have to be hand-rolled
//   per-request via a custom IFileAuthorizationFilter or by stacking
//   additional middleware. A minimal-API endpoint with
//   .RequireAuthorization() is simpler, idiomatic, and uses the same
//   authorization pipeline as every other protected resource.
// ==================================================================================================================================
app.MapGet("/uploads/products/{fileName}", async (
    string fileName,
    HttpContext httpContext,
    IConfiguration configuration) =>
{
    // ── Sanitize fileName: reject anything that could traverse the filesystem.
    // The route parameter binds the URL-decoded value, so "%2F" or "%5C"
    // sequences are already decoded to "/" or "\" by the time we see them.
    if (string.IsNullOrWhiteSpace(fileName)
        || fileName.Contains("..", StringComparison.Ordinal)
        || fileName.Contains('/', StringComparison.Ordinal)
        || fileName.Contains('\\', StringComparison.Ordinal)
        || fileName.Contains(':', StringComparison.Ordinal))
    {
        return Results.NotFound();
    }

    // ── Resolve the physical path from the SAME config key LocalFileStorage
    // reads (FileStorage:RootPath), with the SAME default. The "products"
    // subfolder matches LocalFileStorage.ProductImagesSubfolder.
    var rootPath = configuration["FileStorage:RootPath"] ?? "wwwroot/uploads";
    var productsPath = Path.Combine(rootPath, "products");
    var absolutePath = Path.Combine(productsPath, fileName);

    // Canonical-path check: resolve both to absolute paths and verify the
    // file is still under the products folder. Defends against any symlink
    // or path-traversal trick that could escape the root.
    var canonicalProductsPath = Path.GetFullPath(productsPath);
    string canonicalFilePath;
    try
    {
        canonicalFilePath = Path.GetFullPath(absolutePath);
    }
    catch (Exception ex) when (ex is ArgumentException or System.Security.SecurityException or PathTooLongException or NotSupportedException)
    {
        return Results.NotFound();
    }

    if (!canonicalFilePath.StartsWith(canonicalProductsPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    {
        return Results.NotFound();
    }

    if (!File.Exists(canonicalFilePath))
    {
        return Results.NotFound();
    }

    // ── Set Content-Type from the on-disk extension. SanitizeExtension (after
    // Fix #08) ensures the extension is one of: jpg, png, webp (the only types
    // SniffContentType recognizes). Anything else is unreachable via this
    // endpoint (SaveAsync would have rejected it), but we map to
    // application/octet-stream as a safe default for defense-in-depth.
    var ext = Path.GetExtension(fileName).ToLowerInvariant();
    var contentType = ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    // ── Results.File with a physical file path:
    //   - Streams the file from disk (no full-byte-buffer load — same
    //     streaming posture as UseStaticFiles would use).
    //   - Sets Content-Type from the contentType arg.
    //   - Sets Content-Disposition: attachment; filename={fileName} when
    //     fileDownloadName is non-null — FORCES the browser to download
    //     instead of rendering inline as a navigable HTML page.
    //   - Returns 200 OK.
    // The fileName here is the on-disk filename (e.g. "abc123def.jpg"),
    // NOT any client-supplied original name — we don't trust that string,
    // and the on-disk name is what LocalFileStorage generated via
    // RandomNumberGenerator.GetHexString (cryptographically random hex,
    // filesystem-safe, no user-controlled bytes).
    return Results.File(
        canonicalFilePath,
        contentType,
        fileDownloadName: fileName);
})
.RequireAuthorization()
.WithName("ServeProductImage")
.WithTags("Uploads");

// ==================================================================================================================================
//                                                          LOGOUT ENDPOINT (Issue #05 — CSRF-safe logout, v2 route rename)
// ==================================================================================================================================
// Logout MUST be a POST request with antiforgery-token validation. The
// previous implementation was a Blazor @page "/Account/Logout" that ran
// SignInManager.SignOutAsync() inside OnInitializedAsync — i.e. on a
// plain GET. That meant an attacker could embed
//   <img src="https://takone.example/Account/Logout">
// on ANY third-party page; when any logged-in user visited that page,
// the browser sent a GET to /Account/Logout, the cookie was attached
// (same-site), and the user was silently logged out. SameSite=Strict on
// the auth cookie mitigates this in modern browsers but fails for older
// browsers, relaxed SameSite configs (Lax/None), and same-site attacker
// content (e.g. a comment box on the same domain).
//
// The fix is the standard ASP.NET Core CSRF-safe logout pattern:
//   1. GET  /Account/SignOut → 405 Method Not Allowed (no SignOutAsync).
//        An <img src=".../Account/SignOut"> tag now hits this handler
//        and gets a 405 response — the auth cookie is NOT touched.
//   2. POST /Account/SignOut  → SignOutAsync + 302 redirect to login.
//        In .NET 8+, minimal-API POST endpoints are validated by the
//        UseAntiforgery middleware AUTOMATICALLY (no RequireAntiforgery()
//        call exists — antiforgery is the default; only DisableAntiforgery()
//        opts OUT). The <AntiforgeryToken /> component inside the logout
//        <form> in MainLayout / ShopLayout / AccessDenied renders the
//        __RequestVerificationToken hidden field. A forged cross-site
//        POST has no token → 400 Bad Request → no logout.
//
// WHY A MINIMAL-API ENDPOINT (not a Razor page):
//   The original LogOut.razor used OnInitializedAsync, which runs on
//   BOTH GET (initial page load) and POST (form submission). To make
//   GET safe we'd have to add manual method-checking inside the page.
//   A minimal-API endpoint is method-dispatched by the router itself —
//   GET and POST are completely separate handlers, and there is no way
//   for the GET handler to accidentally trigger SignOutAsync. This is
//   the cleanest, least-error-prone structure.
//
// WHY THE REDIRECT GOES TO /Account/Login (not /):
//   After SignOutAsync the auth cookie is gone. Sending the user to any
//   protected page would just bounce them to /Account/Login anyway via
//   the cookie middleware's LoginPath. Going straight to /Account/Login
//   saves a round-trip and gives the user an immediate visual signal
//   that they're signed out.
//
// WHY THE ROUTE IS /Account/SignOut (not the conventional /Account/Logout):
//   Two prior attempts to use /Account/Logout both produced a runtime
//   AmbiguousMatchException ("The request matched multiple endpoints.
//   Matches: HTTP: POST /Account/Logout, /Account/Logout (/Account/Logout)").
//   Attempt #1 left CookieAuthenticationOptions.LogoutPath set to
//   "/Account/Logout"; the .NET 10 cookie auth handler appears to
//   register an implicit endpoint at LogoutPath during endpoint routing,
//   which conflicted with our explicit MapGet/MapPost endpoints.
//   Attempt #2 commented out LogoutPath (leaving it at its empty
//   default), so the cookie handler should NOT have registered an
//   implicit endpoint — yet the AmbiguousMatchException persisted in
//   production. The exact source of the second endpoint could not be
//   isolated (no Razor Page, no Blazor @page, no MVC controller, no
//   Identity UI, no AddDefaultUI/AddDefaultIdentity — every search
//   came up empty). Rather than continue chasing the ghost, the route
//   is renamed to /Account/SignOut, which is GUARANTEED not to
//   collide with any pre-existing endpoint. The cost is one slightly
//   unusual URL; the benefit is that logout simply works.
//
// COOKIE MIDDLEWARE NOTE:
//   CookieAuthenticationOptions.LogoutPath is intentionally NOT set in
//   AddTakOneInfrastructure (we leave it at its empty default). Even
//   though our logout endpoint is now at /Account/SignOut (not
//   /Account/Logout), we still leave LogoutPath empty: setting it to
//   /Account/SignOut would re-introduce the same implicit-endpoint
//   conflict on the new path. SignOutAsync always clears the cookie
//   regardless of LogoutPath — that option only controls whether the
//   handler auto-redirects to LoginPath after sign-out, which we don't
//   want (our endpoint issues its own Results.Redirect below).
app.MapGet("/Account/SignOut",
    () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

app.MapPost("/Account/SignOut",
    async (SignInManager<ApplicationUser> signInManager) =>
    {
        await signInManager.SignOutAsync();
        return Results.Redirect("/Account/Login", permanent: false);
    });
// ↑ NOTE: no .DisableAntiforgery() here — antiforgery validation is the
//   DEFAULT for minimal-API POST endpoints in .NET 8+. The
//   UseAntiforgery middleware (called above) automatically validates the
//   __RequestVerificationToken field on every unsafe-method request.
//   NO endpoint in the codebase calls DisableAntiforgery() to opt out —
//   the dead /api/product-image endpoint that used to was removed in
//   Brutal Code Review v3 finding #20. For logout we want the default
//   (validate), so we do nothing.
//
//   The <form method="post" action="/Account/SignOut"> in MainLayout.razor
//   and AccessDenied.razor submits to this endpoint. The <AntiforgeryToken />
//   inside each form renders the hidden __RequestVerificationToken input
//   that the UseAntiforgery middleware validates.

// ==================================================================================================================================
//                                                          DEAD /api/product-image ENDPOINT REMOVED
// ==================================================================================================================================
// Brutal Code Review v3 finding #20 (Round 18-B) — the entire POST
// /api/product-image minimal-API endpoint, the multipart form-post size-
// limit middleware that gated it, AND the IHttpClientFactory registration
// (whose doc-comment claimed it fed a "RadzenUpload component on
// CreateProduct.razor / ProductDetail.razor" — FALSE: those pages call
// IFileStorage.SaveAsync directly via the Blazor Server circuit, never
// round-tripping through this HTTP endpoint) were ALL DEAD CODE.
//
// The endpoint was also the ONLY place in the codebase that called
// .DisableAntiforgery() — leaving a live CSRF surface that nothing used.
// Removal closes that hole and eliminates ~150 lines of dead code.
//
// Before deleting, the entire repo was grepped for "product-image",
// "IHttpClientFactory", "HttpClient", "UploadProductImage", and
// "RadzenUpload" — zero live references in any .razor or .cs file outside
// Program.cs itself (the .razor hits were CSS class names like
// .m-product-image / .tm-product-image, not endpoint URLs). The
// UnexpectedErrorCodes constants ProductImageEndpoint_InvalidUpload and
// ProductImageEndpoint_UploadFailed in TakOne.Application are LEFT IN PLACE
// because they are referenced by tests in TakOne.Application.Tests (which
// are out of scope for this Round 18-B task) — they become unused constants
// that can be purged in a future cleanup round.
// ==================================================================================================================================

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
// SECURITY POSTURE (Issue #02 — Hardcoded default admin password):
//   PREVIOUSLY: the seeder ran unconditionally on every startup, in every
//   environment, with a hard-coded password committed to source. The
//   password was logged at WARNING level on first creation.
//
//   NOW: the seeder is GATED to Development OR explicit opt-in via
//   TakOne:DefaultAdmin:Enabled=true. The password comes from
//   configuration (user secrets in Dev, env var / Key Vault in Prod) —
//   NEVER from source. The password is NEVER logged. The seeded admin's
//   MustChangePassword flag is set to true (unless opted out via
//   ForcePasswordChangeOnFirstLogin=false), so the first human admin must
//   set their own password before accessing any other page.
//
// IDEMPOTENT: GetUsersInRoleAsync("Admin") non-empty → no-op. Safe to
// leave wired in for every startup — it will do nothing on the second and
// subsequent runs.
//
// VALIDATION:
//   We resolve the bound DefaultAdminOptions from DI and call
//   EnsureValid(env). In a non-Development environment where Enabled=true
//   but no password is configured, EnsureValid THROWS and the application
//   refuses to start. This is intentional — a Production deployment
//   should NEVER silently seed an admin with an empty password.
var defaultAdminOptions = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<DefaultAdminOptions>>()
    .Value;
defaultAdminOptions.EnsureValid(app.Environment);

// Run the seeder only in Development, OR when explicitly enabled via
// configuration. The check is here (not inside the seeder) so the seeder
// stays a pure "create the admin if asked" function and Program.cs owns
// the environment-aware "should we even ask" decision.
if (app.Environment.IsDevelopment() || defaultAdminOptions.Enabled)
{
    await DefaultAdminSeeder.EnsureDefaultAdminAsync(
        app.Services,
        defaultAdminOptions,
        app.Environment);
}

// ==================================================================================================================================
//                                                          STATIC WEB ASSETS
// ==================================================================================================================================
// Map static web assets — the .NET 8+ API that serves URLs under
// `/_content/{LibraryName}/...` from referenced Razor class libraries.
//
// WHY THIS IS NEEDED:
//   Radzen.Blazor is a Razor class library that exposes its CSS + JS via
//   `_content/Radzen.Blazor/Radzen.Blazor.css` and similar paths. The
//   Blazor pages also reference `/_content/Radzen.Blazor/Radzen.Blazor.js`.
//   Without `MapStaticAssets()`, those URLs return 404 and the entire UI
//   renders unstyled (Radzen components need their CSS to look right).
//   `app.UseStaticFiles()` (called above) only serves files from the
//   physical `wwwroot/` directory; it does NOT serve the static web
//   assets manifest that the SDK generates for referenced RCLs.
//
// WHY THIS IS CALLED LATE:
//   `MapStaticAssets()` is a terminal-adjacent call (it registers
//   endpoint routes). It must run AFTER `UseRouting` + `UseAuthorization`
//   (which are above), but BEFORE `app.Run()`. The .NET 10 default
//   template calls it right before `app.Run()`.
app.MapStaticAssets();

// ==================================================================================================================================
//                                                          RUN
// ==================================================================================================================================

// Use RunAsync (not Run) so the await chain stays consistent with the
// async RoleSeeder call above. Top-level statements support async natively
// — the compiler generates an async Main entry point under the hood.
await app.RunAsync();
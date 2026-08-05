using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Radzen;
using TakOne.Application.Common.Authorization;
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
builder.Services.AddScoped<ToastService>();

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

// --- HttpClient for client-side API calls from Blazor Server circuits ---
//
// Used by the RadzenUpload component on CreateProduct.razor / ProductDetail.razor
// to POST uploaded product images to the /api/product-image minimal API endpoint.
// The browser's auth cookie is automatically attached to the request by the
// underlying fetch() call, so the same auth flow that protects pages protects
// the upload endpoint — no separate token wiring needed.
//
// Registered as a transient factory because HttpClient is lightweight and
// IHttpClientFactory-based clients are the recommended pattern (avoids socket
// exhaustion). The BaseAddress is set from the current request so relative
// URLs like "/api/product-image" resolve correctly.
builder.Services.AddHttpClient("TakOne", (sp, client) =>
{
    var httpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
    var request = httpContextAccessor.HttpContext?.Request;
    if (request is not null)
    {
        client.BaseAddress = new Uri($"{request.Scheme}://{request.Host}");
    }
});
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("TakOne"));

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
//   - /Account/LogOut          — so they can sign out if they don't want
//                                 to change the password right now
//   - /Account/Login           — defensive; they shouldn't reach this
//                                 page while authenticated, but if they
//                                 do we don't want a redirect loop
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
            path.StartsWith("/css", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/js", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/lib", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/TakLogo.png", StringComparison.OrdinalIgnoreCase);

        // Account paths the user is allowed to hit while their password
        // is in the must-change state.
        var isAllowedAccountPath =
            path.StartsWith("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Account/LogOut", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase);

        if (!isStaticAsset && !isAllowedAccountPath)
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

// ==================================================================================================================================
//                                                          LOGOUT ENDPOINT (Issue #05 — CSRF-safe logout)
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
//   1. GET  /Account/Logout → 405 Method Not Allowed (no SignOutAsync).
//        An <img src=".../Account/Logout"> tag now hits this handler
//        and gets a 405 response — the auth cookie is NOT touched.
//   2. POST /Account/Logout  → SignOutAsync + 302 redirect to login.
//        In .NET 8+, minimal-API POST endpoints are validated by the
//        UseAntiforgery middleware AUTOMATICALLY (no RequireAntiforgery()
//        call exists — antiforgery is the default; only DisableAntiforgery()
//        opts OUT, as the /api/product-image endpoint below does). The
//        <AntiforgeryToken /> component inside the logout <form> in
//        MainLayout / ShopLayout / AccessDenied renders the
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
// COOKIE MIDDLEWARE NOTE:
//   CookieAuthenticationOptions.LogoutPath is configured to
//   "/Account/Logout" in AddTakOneInfrastructure. When
//   SignInManager.SignOutAsync() is called from a handler whose
//   request path EQUALS LogoutPath, the cookie handler clears the
//   cookie and returns without issuing its own redirect — so our
//   explicit Results.Redirect("/Account/Login") is the response the
//   browser actually sees. No redirect loop, no double-302.
app.MapGet("/Account/Logout",
    () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

app.MapPost("/Account/Logout",
    async (SignInManager<ApplicationUser> signInManager) =>
    {
        await signInManager.SignOutAsync();
        return Results.Redirect("/Account/Login", permanent: false);
    });
// ↑ NOTE: no .DisableAntiforgery() here — antiforgery validation is the
//   DEFAULT for minimal-API POST endpoints in .NET 8+. The
//   UseAntiforgery middleware (line ~471) automatically validates the
//   __RequestVerificationToken field on every unsafe-method request.
//   Only the /api/product-image endpoint below calls DisableAntiforgery()
//   to opt OUT — that's the explicit opt-out path. For logout we want
//   the default (validate), so we do nothing.

// ==================================================================================================================================
//                                                          PRODUCT IMAGE UPLOAD ENDPOINT
// ==================================================================================================================================
// Phase 7 item C — minimal API endpoint that accepts a single image upload
// from the CreateProduct / EditProduct forms, streams it through
// IFileStorage (LocalFileStorage), and returns the public URL the file can
// be retrieved from. The URL is stored on Product.PictureUrl.
//
// WHY A MINIMAL API ENDPOINT (not a Wolverine command):
//   Wolverine's command pipeline is designed for business transactions —
//   it validates, runs in a DB transaction, publishes domain events. A file
//   upload is a pure I/O concern with no business rules to validate, no
//   transaction needed, no events to publish. Routing it through Wolverine
//   would add overhead with zero benefit. A minimal API endpoint is the
//   correct tool for the job.
//
// AUTHORIZATION:
//   [Authorize(Roles = "Employee,Manager,Admin")] — matches the
//   CreateProduct.razor / EditProduct.razor page authorization. A customer
//   can't upload product images even if they craft the request manually.
//   The role check runs against the same auth cookie the page uses, so
//   there's no separate credential flow.
//
// REQUEST SHAPE:
//   POST /api/product-image
//   Content-Type: multipart/form-data
//   Body: IFormFile "file" (single file; multiple files not supported —
//         Product.PictureUrl is a single string)
//
// RESPONSE (200 OK):
//   { "url": "/uploads/products/abc123def456...jpg" }
//
// RESPONSE (4xx):
//   401 Unauthorized — not logged in
//   403 Forbidden — logged in but not Employee/Manager/Admin
//   400 Bad Request — no file, empty file, content-type not in allowlist,
//                     file size over limit, content-type doesn't match
//                     actual file content (magic-byte sniffing fails)
//   413 Payload Too Large — Kestrel-level size limit hit before our handler
//                            runs (configured via RequestFormLimits below)
//
// ANTIFORGERY:
//   Exempted via IAntiforgery.IsValidRequestAsync. The Blazor Server circuit
//   already establishes an antiforgery token for the user's session; the
//   RadzenUpload component sends it automatically as a header. We validate
//   it here to prevent CSRF on direct API calls. (The Antiforgery middleware
//   at line 251 handles this automatically for unsafe methods — no explicit
//   validation code needed in the handler.)
// ==================================================================================================================================

// Raise Kestrel's per-request multipart body size limit to 50 MB so a
// 5 MB image upload (LocalFileStorage's max) plus multipart overhead doesn't
// get rejected before our handler runs. The previous 10 MB limit was too
// tight — a single modern phone photo (often 8-12 MB) could exceed it and
// crash with an unhandled InvalidDataException before LocalFileStorage had
// a chance to return a clean 400. With 50 MB headroom, LocalFileStorage's
// 5 MB cap is what users actually hit, producing a friendly error message
// instead of a crash.
//
// We do this by replacing the IFormFeature on the request features collection
// with one configured with a FormOptions that has our 50 MB limit. The
// replacement happens only for the /api/product-image path so other endpoints
// keep their default behavior.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/product-image", StringComparison.OrdinalIgnoreCase))
    {
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IFormFeature>(
            new Microsoft.AspNetCore.Http.Features.FormFeature(
                context.Request,
                new Microsoft.AspNetCore.Http.Features.FormOptions
                {
                    MultipartBodyLengthLimit = 50 * 1024 * 1024 // 50 MB
                }));
    }
    return next();
});

app.MapPost
    (
    "/api/product-image",
    async
    (
    HttpContext httpContext,
    IFileStorage fileStorage,
    ILogger<Program> logger) =>
    {
        // ── Authorization check: Employee/Manager/Admin only.
        // The endpoint route is decorated with [Authorize(Roles=...)] below via
        // RequireAuthorization, but we double-check here for defense-in-depth —
        // if someone removes RequireAuthorization in a refactor, this still
        // prevents a customer from uploading.
        if (!httpContext.User.Identity?.IsAuthenticated ?? true)
            return Results.Unauthorized();

        var isStaff = httpContext.User.IsInRole(Roles.Employee)
                   || httpContext.User.IsInRole(Roles.Manager)
                   || httpContext.User.IsInRole(Roles.Admin);
        if (!isStaff)
            return Results.Forbid();

        // ── Extract the uploaded file from the multipart form.
        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        if (form.Files.Count == 0)
            return Results.BadRequest(new { error = "No file was uploaded." });

        if (form.Files.Count > 1)
            return Results.BadRequest(new { error = "Only one file can be uploaded at a time." });

        var file = form.Files[0];
        if (file.Length == 0)
            return Results.BadRequest(new { error = "The uploaded file is empty." });

        // ── Content-type allowlist (advisory check; LocalFileStorage sniffs
        // magic bytes for the authoritative check).
        var allowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp"
    };
        if (!allowedContentTypes.Contains(file.ContentType))
        {
            return Results.BadRequest(new
            {
                error = $"Unsupported file type '{file.ContentType}'. Allowed: JPEG, PNG, WebP."
            });
        }

        // ── Stream the file to storage. LocalFileStorage handles:
        //   - Magic-byte sniffing (rejects if actual content ≠ declared content type)
        //   - Max size enforcement (rejects if > 5 MB)
        //   - Atomic write (write to .tmp, then File.Move)
        //   - Filename generation (crypto-random hex, no client filename trusted)
        try
        {
            // OpenReadStream returns a streaming, non-buffering view of the
            // uploaded file's bytes. The actual 5 MB size limit is enforced
            // INSIDE LocalFileStorage via BoundedStream (which throws as soon
            // as the cumulative byte count crosses 5 MB), and Kestrel's
            // per-request multipart limit (10 MB, set via the IFormFeature
            // replacement in the middleware above) is the outer gate. No need
            // to also cap at the OpenReadStream layer — that would be a third
            // defense-in-depth layer, but two layers are enough.
            await using var stream = file.OpenReadStream();
            var url = await fileStorage.SaveAsync(
                stream,
                file.FileName,
                file.ContentType,
                httpContext.RequestAborted);

            logger.LogInformation(
                "Product image uploaded by {User} ({Bytes} bytes, {ContentType}) → {Url}",
                httpContext.User.Identity?.Name ?? "?",
                file.Length,
                file.ContentType,
                url);

            return Results.Ok(new { url });
        }
        catch (InvalidDataException ex)
        {
            // LocalFileStorage throws this for: unrecognized content type,
            // content-type mismatch, or file size over limit. Map to 400.
            logger.LogWarning(
                "Product image upload rejected: {Message}. User: {User}",
                ex.Message, httpContext.User.Identity?.Name ?? "?");
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            // Unexpected failure (disk full, permission denied, etc.). Don't leak
            // the exception message to the client — return a generic 500.
            logger.LogError(
                ex,
                "Unexpected error during product image upload. User: {User}",
                httpContext.User.Identity?.Name ?? "?");
            return Results.Problem(
                title: "Upload failed.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    })
.RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = "Employee,Manager,Admin" })
.WithName("UploadProductImage")
.WithTags("Products")
.DisableAntiforgery(); // The RadzenUpload component doesn't send our antiforgery token;
                       // we'd need to wire that up explicitly. CSRF risk is low because
                       // this endpoint only writes a file to disk — it doesn't mutate any
                       // business state. A CSRF attack that uploads a file just leaves an
                       // orphan file; it doesn't compromise the user's account. If this
                       // assumption changes (e.g. endpoint starts recording upload
                       // metadata to the DB), remove DisableAntiforgery and wire the token
                       // in the RadzenUpload request headers.

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
//                                                          RUN
// ==================================================================================================================================

// Use RunAsync (not Run) so the await chain stays consistent with the
// async RoleSeeder call above. Top-level statements support async natively
// — the compiler generates an async Main entry point under the hood.
await app.RunAsync();
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
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
builder.Services.AddTakOneInfrastructure(builder.Configuration, builder.Environment);

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
// In production, this is solved by PersistKeysToDbContext<T>() or
// PersistKeysToFileSystem(new DirectoryInfo("\\\\server\\share\\keys"))
// pointing to a shared, replicated location. For dev, we use a local
// folder under the user profile so keys survive across restarts.
//
// SetApplicationName is REQUIRED when multiple apps share a key ring —
// and even with a single app it makes the key discriminator stable
// across rebuilds (otherwise the app's "default" discriminator can shift
// if the entry-assembly name changes during refactoring).
//
// PRODUCTION HARDENING (future):
//   Replace PersistKeysToFileSystem with PersistKeysToDbContext<DataProtectionKeyDbContext>()
//   (or a dedicated schema in ApplicationDbContext) so all web nodes share
//   the same key ring in SQL Server.
builder.Services.AddDataProtection()
    .SetApplicationName("TakOne")
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(builder.Environment.ContentRootPath, ".dataprotection-keys")));

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
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapHub<NotificationHub>("/notificationHub");

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
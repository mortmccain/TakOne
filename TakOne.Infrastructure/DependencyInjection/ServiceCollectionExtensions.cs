using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Configuration;
using TakOne.Infrastructure.Identity;
using TakOne.Infrastructure.Persistence;
using TakOne.Infrastructure.Persistence.Repositories;
using TakOne.Infrastructure.Services;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace TakOne.Infrastructure.DependencyInjection;

/// <summary>
/// Composition root for the TakOne Infrastructure layer.
///
/// This is the ONLY extension method the WebUI's Program.cs needs to call to
/// wire up everything the Infrastructure layer provides:
///
///   - <see cref="ApplicationDbContext"/> (registered with EF Core + SQL Server,
///     sharing one connection per scope with Wolverine's outbox so business
///     changes and outbox entries commit atomically)
///   - ASP.NET Core Identity (<c>AddIdentity&lt;ApplicationUser,
///     IdentityRole&lt;Guid&gt;&gt;.AddEntityFrameworkStores&lt;ApplicationDbContext&gt;()</c>)
///   - The 4 repository implementations (Category, Product, Sale, User) — Scoped
///   - <see cref="IUnitOfWork"/> → <see cref="UnitOfWork"/> — Scoped
///   - <see cref="ISaleNumberGenerator"/> → <see cref="SaleNumberGenerator"/> — Scoped
///   - <see cref="IUserAccountService"/> → <see cref="UserAccountService"/> — Scoped
///   - Wolverine SQL Server message store
///     (<c>opts.PersistMessagesWithSqlServer(connectionString)</c>) — creates
///     the <c>wolverine_messages</c> table and durably persists every locally
///     dispatched message until processed. Uses the SAME connection string
///     as <see cref="ApplicationDbContext"/>, so outbox entries commit in
///     the SAME transaction as business changes.
///   - Wolverine durable local queues policy
///     (<c>opts.Policies.UseDurableLocalQueues()</c>) — enrolls ALL local
///     queues into durable inbox/outbox processing. Without this, messages
///     are in-memory only and lost on process restart.
///   - Wolverine EF Core transactional middleware
///     (<c>opts.UseEntityFrameworkCoreTransactions()</c> +
///     <c>opts.Policies.AutoApplyTransactions()</c>) — the transactional link
///     between the DbContext and the outbox. Combined with (a) and (b) above,
///     this completes the transactional-outbox chain.
///   - Wolverine domain-event scraper
///     (<c>opts.PublishDomainEventsFromEntityFrameworkCore&lt;AggregateRoot,
///     BaseDomainEvent&gt;(agg =&gt; agg.DomainEvents)</c>) — publishes all
///     domain events raised by tracked aggregates through the enrolled outbox
///     at SaveChanges time. Replaces what would otherwise be a hand-rolled
///     <c>ISaveChangesInterceptor</c> (which has tricky captive-dependency and
///     timing issues that Wolverine's built-in scraper handles correctly).
///
/// ARCHITECTURAL NOTE — WHY THE FULL OUTBOX WIRING LIVES HERE (not in Application):
///   The Application layer's <c>AddTakOneApplication</c> configures ONLY the
///   engine-agnostic Wolverine concerns (handler discovery, middleware,
///   FluentValidation). Everything that touches a concrete persistence engine
///   (SQL Server message store, EF Core transactional middleware, domain-event
///   scraper) lives here. This keeps the Application layer free of any
///   <c>WolverineFx.SqlServer</c> / <c>WolverineFx.EntityFrameworkCore</c>
///   package references — the application code is identical whether the
///   message store is SQL Server, Postgres, or in-memory, and whether the
///   persistence stack is EF Core or something else.
///
/// WHAT IS NOT REGISTERED HERE:
///   - <see cref="TakOne.Application.Common.Interfaces.ICurrentUserService"/>
///     — the real implementation depends on IHttpContextAccessor and lives in
///     the WebUI layer.
///
/// ORDER OF CALLS IN Program.cs:
///   1. <c>builder.Services.AddTakOneApplication(builder.Configuration)</c>
///   2. <c>builder.Services.AddTakOneInfrastructure(builder.Configuration)</c>
///   3. <c>builder.Host.UseWolverine(opts =&gt; { ... })</c> — the lambda
///      invokes <see cref="ConfigureWolverine"/> on BOTH the Application
///      and Infrastructure <c>ServiceCollectionExtensions</c> classes,
///      passing the <c>opts</c> instance directly. This is REQUIRED:
///      Wolverine does NOT read its options from the
///      <c>IOptions&lt;WolverineOptions&gt;</c> pipeline, so
///      <c>services.Configure&lt;WolverineOptions&gt;</c> does not work.
///      The <c>ConfigureWolverine</c> methods are the ONLY way to apply
///      Application- and Infrastructure-layer Wolverine configuration.
///
/// CONNECTION STRING HANDLING:
///   This method takes <c>IConfiguration</c> (not a connection string
///   parameter) and reads the connection string internally via
///   <see cref="TakOneDatabaseOptions"/>. The connection string never appears
///   in any method signature, log, or error message. The Application layer's
///   <c>AddTakOneApplication</c> does NOT read the connection string at all
///   — only this method does (for both the DbContext and the Wolverine
///   message store).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Infrastructure-layer services to the DI container and
    /// configures the COMPLETE Wolverine transactional-outbox wiring (SQL
    /// Server message store + durable local queues + EF Core transactional
    /// middleware + domain-event scraper) plus domain-event-dispatch.
    /// </summary>
    /// <param name="services">
    /// The DI container being configured. Typically <c>builder.Services</c>
    /// in Program.cs.
    /// </param>
    /// <param name="configuration">
    /// Application configuration. The database connection string is read
    /// internally from the <c>TakOne:Database:ConnectionString</c> key — it
    /// is never accepted as a method parameter.
    /// </param>
    /// <returns>
    /// The modified <see cref="IServiceCollection"/> (so callers can chain
    /// further DI calls if they wish).
    /// </returns>
    public static IServiceCollection AddTakOneInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment)
    {
        // ------------------------------------------------------------------
        // 0. Argument guards.
        // ------------------------------------------------------------------
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        // ------------------------------------------------------------------
        // 1. Bind TakOneDatabaseOptions from configuration.
        //
        //    This is the SINGLE binding site for TakOneDatabaseOptions — the
        //    Application layer no longer binds it (since the SQL Server
        //    message store call was moved here). Infrastructure owns the
        //    connection string end-to-end: it's used for the DbContext (sec 2)
        //    AND for the Wolverine SQL Server message store (sec 6a).
        //
        //    We validate IMMEDIATELY so a missing connection string fails at
        //    startup with a clear message, not at first request with an
        //    opaque SqlException.
        // ------------------------------------------------------------------
        services.Configure<TakOneDatabaseOptions>(
            configuration.GetSection(TakOneDatabaseOptions.SectionName));

        // Read the connection string NOW (at startup) for the DbContext
        // registration AND the Wolverine message store below. We can't use
        // IOptions<T> here because IOptions<T> is resolved at runtime, not
        // at configuration time. Reading from IConfiguration directly is
        // safe — IConfiguration is a singleton available at startup.
        //
        // SECURITY: this local variable captures the connection string. It
        // is passed to opts.UseSqlServer(...) and to
        // opts.PersistMessagesWithSqlServer(...), and never leaves this
        // method. It is never logged, never exposed as a method parameter,
        // never stored in a static field.
        var databaseOptions = new TakOneDatabaseOptions
        {
            ConnectionString = configuration
                .GetSection(TakOneDatabaseOptions.SectionName)
                .GetValue<string>(nameof(TakOneDatabaseOptions.ConnectionString))
                ?? string.Empty
        };
        databaseOptions.EnsureValid();

        // ------------------------------------------------------------------
        // 2. Register ApplicationDbContext.
        //
        //    Scoped lifetime (the default for AddDbContext) — one instance per
        //    HTTP request / Wolverine handler invocation. This is critical:
        //    the same DbContext instance must be shared by the repositories,
        //    IUnitOfWork, IUserAccountService, AND Wolverine's outbox so they
        //    all participate in the same transaction.
        //
        //    We use the (IServiceProvider, DbContextOptionsBuilder) overload
        //    so we can resolve IOptions<TakOneDatabaseOptions> inside the
        //    lambda. (The simpler Action<DbContextOptionsBuilder> overload
        //    doesn't give us access to DI.)
        //
        //    We do NOT call opts.AddInterceptors(...) here. Originally we
        //    planned to use a custom ISaveChangesInterceptor to publish
        //    domain events. Research on Wolverine 6.20.0's actual API surface
        //    revealed that:
        //      a) IMessageBus is Scoped, but DbContextOptions (which holds
        //         interceptors) is Singleton — a Singleton-bound interceptor
        //         cannot safely constructor-inject a Scoped IMessageBus
        //         (captive dependency).
        //      b) Calling IMessageBus.PublishAsync from inside
        //         SavingChangesAsync has timing issues — newly added
        //         OutgoingMessage entities may not be in the same SaveChanges
        //         round-trip.
        //    Wolverine has a built-in solution: PublishDomainEventsFromEntityFrameworkCore
        //    (configured in section 6 below). It scrapes events from the
        //    ChangeTracker AFTER SaveChanges succeeds but BEFORE the
        //    transaction commits, and publishes them through the enrolled
        //    outbox. No custom interceptor needed.
        // ------------------------------------------------------------------
        services.AddDbContext<ApplicationDbContext>(
            (serviceProvider, options) =>
            {
                // Resolve the bound options to get the connection string.
                // (Reading from IOptions<T> instead of IConfiguration directly
                // here so that if someone overrides TakOneDatabaseOptions via
                // services.Configure, our DbContext picks up the override.)
                var dbOpts = serviceProvider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<TakOneDatabaseOptions>>()
                    .Value;

                options.UseSqlServer(dbOpts.ConnectionString);

                // NOTE: we deliberately do NOT call options.AddInterceptors(...)
                // here. See the comment in section 6 below for the rationale.
            },
            ServiceLifetime.Scoped,   // contextLifetime  — one DbContext per scope
            ServiceLifetime.Singleton // optionsLifetime  — options are immutable, safe to share
            );

        // ------------------------------------------------------------------
        // 3. ASP.NET Core Identity.
        //
        //    AddIdentity<ApplicationUser, IdentityRole<Guid>>() registers:
        //      - UserManager<ApplicationUser>
        //      - RoleManager<IdentityRole<Guid>>
        //      - SignInManager<ApplicationUser>
        //      - IHttpContextAccessor (if not already registered)
        //      - Default IPasswordValidator, IUserNameValidator, IEmailSender,
        //        ILookupNormalizer, IPasswordHasher, IUserValidator, etc.
        //
        //    AddEntityFrameworkStores<ApplicationDbContext>() wires Identity
        //    to use OUR ApplicationDbContext (not a separate IdentityDbContext).
        //    This is what lets a User creation + role assignment commit in
        //    the SAME SaveChanges as a Sale creation — one transaction, no
        //    MSDTC.
        //
        //    AddDefaultTokenProviders() — registers the standard token
        //    generators for password reset, email confirmation, two-factor
        //    auth, etc. (We don't use the email-confirmation flow for
        //    admin-created accounts, but the token providers are needed for
        //    any future self-service features.)
        //
        //    IDENTITY OPTIONS BINDING (Concern G — locked in):
        //      Options are bound from the "TakOne:Identity" section of
        //      appsettings.json — NOT hard-coded. This lets ops tune the
        //      password policy, lockout window, etc. without a code redeploy.
        //      The bound values OVERRIDE the Identity defaults; we do not
        //      also call services.Configure<IdentityOptions> because the
        //      AddIdentity(...) lambda runs FIRST and would be overwritten
        //      by post-hoc Configure<IdentityOptions> calls in subtle ways.
        //
        //    NOTE on the binding pattern:
        //      We bind each subsection (Password, Lockout, User, SignIn) to
        //      the corresponding nested options object on `options`. This is
        //      necessary because `services.Configure<IdentityOptions>(section)`
        //      binds the WHOLE IdentityOptions, and Bind() on a sub-object
        //      is more explicit about which subsection goes where. The
        //      TimeSpan values (e.g. "00:15:00" for DefaultLockoutTimeSpan)
        //      parse correctly via Microsoft.Extensions.Configuration.Binder.
        // ------------------------------------------------------------------
        var identitySection = configuration.GetSection("TakOne:Identity");

        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            // Bind each subsection from appsettings.json. If a section
            // is missing in appsettings, the Identity defaults remain
            // in place (the Bind call is a no-op on missing sections).
            identitySection.GetSection("Password").Bind(options.Password);
            identitySection.GetSection("Lockout").Bind(options.Lockout);
            identitySection.GetSection("User").Bind(options.User);
            identitySection.GetSection("SignIn").Bind(options.SignIn);
        })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders()
            // v6.2: Localize Identity's built-in error messages
            // (PasswordRequiresNonAlphanumeric, DuplicateUserName,
            // InvalidToken, etc.) at the source. The describer resolves
            // strings from IdentityErrorMessages.{culture}.resx via
            // IStringLocalizer<IdentityErrorMessages>, so every
            // UserManager / SignInManager call site returns the message
            // in the request's current UI culture (fa-IR or en-US).
            // Without this, weak-password errors came back in English
            // even when the rest of the page was Persian.
            .AddErrorDescriber<TakOneIdentityErrorDescriber>();

        // ------------------------------------------------------------------
        // 3b. Cookie auth configuration (Concern F — locked in).
        //
        //     ConfigureApplicationCookie is the API for tuning the auth
        //     cookie that AddIdentity registered by default. Calling it
        //     AFTER AddIdentity lets us override the defaults without
        //     losing the rest of Identity's wiring.
        //
        //     LOCKED-IN COOKIE POLICY (roadmap Section 1, decision 3):
        //       - HttpOnly = true        → JS can't read the cookie (XSS hardening)
        //       - SecurePolicy = Always  → only sent over HTTPS (even in Dev —
        //                                   use HTTPS dev certs)
        //       - SameSite = Strict      → not sent on cross-site requests (CSRF
        //                                   hardening; the trade-off is that
        //                                   following a link from another site
        //                                   to TakOne won't carry the cookie,
        //                                   so the user appears logged-out —
        //                                   acceptable for an enterprise app)
        //       - SlidingExpiration      → activity extends the cookie's life
        //                                   (each request resets the expiry
        //                                   counter, up to the absolute max)
        //       - Expiration             → 1 hour by default (from
        //                                   TakOne:Auth:CookieExpiryHours in
        //                                   appsettings.json). This is short
        //                                   for an enterprise app — the
        //                                   session-expiry warning toast at
        //                                   55 minutes gives the user a 5-min
        //                                   heads-up to save their work.
        //       - LoginPath              → /Account/Login (the static-rendered
        //                                   login page; if Blazor tried to
        //                                   handle this we'd have the
        //                                   "cookie not visible to circuit"
        //                                   problem documented in
        //                                   RedirectToLogin.razor)
        //       - LogoutPath             → intentionally NOT set. Logout is
        //                                   handled by the explicit minimal-API
        //                                   POST endpoint at /Account/SignOut
        //                                   in Program.cs (the route was
        //                                   renamed from /Account/Logout to
        //                                   /Account/SignOut to sidestep a
        //                                   persistent AmbiguousMatchException
        //                                   — see Program.cs LOGOUT ENDPOINT
        //                                   section for the full history).
        //                                   Setting LogoutPath (to ANY value)
        //                                   caused .NET 10's cookie auth
        //                                   handler to register an implicit
        //                                   endpoint at that path that
        //                                   conflicted with the explicit
        //                                   endpoint and threw
        //                                   AmbiguousMatchException when the
        //                                   logout button was clicked.
        //       - AccessDeniedPath       → /Account/AccessDenied
        //                                   (the page shown when
        //                                   AuthorizeRouteView.NotAuthorized
        //                                   fires for an AUTHENTICATED user
        //                                   who lacks the role — distinct
        //                                   from the NotAuthorized →
        //                                   RedirectToLogin flow for
        //                                   UNAUTHENTICATED users)
        //       - Cookie.Name            → "TakOne.Auth" (avoids the default
        //                                   ".AspNetCore.Cookies" name which
        //                                   is shared across multiple apps
        //                                   on the same domain — using a
        //                                   custom name prevents cookie
        //                                   collision if TakOne is ever
        //                                   hosted alongside other ASP.NET
        //                                   Core apps on the same origin)
        // ------------------------------------------------------------------
        var cookieExpiryHours = configuration
            .GetSection("TakOne:Auth:CookieExpiryHours")
            .Get<int>();
        if (cookieExpiryHours <= 0) cookieExpiryHours = 1; // safe default

        var slidingExpiration = configuration
            .GetSection("TakOne:Auth:CookieSlidingExpiration")
            .Get<bool?>() ?? true; // safe default

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "TakOne.Auth";
            options.Cookie.HttpOnly = true;

            // SecurePolicy: Always in production (HTTPS-only), SameAsRequest
            // in Development so the cookie works over HTTP when the dev
            // HTTPS cert isn't trusted (common on first-run dev machines).
            //
            // SYMPTOM if this is Always in dev and the developer hits the
            // app over HTTP: login POST succeeds, cookie is Set on the
            // response with the Secure flag, but the browser refuses to
            // send it back on the next (HTTP) request. The user is
            // immediately redirected back to /Account/Login with the
            // info-style "LoginRequired" banner — looks like "login failed
            // with no error".
            //
            // We capture hostEnvironment at registration time (passed in
            // from Program.cs). Production keeps SecurePolicy.Always
            // (hardened).
            options.Cookie.SecurePolicy = hostEnvironment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            options.Cookie.SameSite = SameSiteMode.Strict;

            options.ExpireTimeSpan = TimeSpan.FromHours(cookieExpiryHours);
            options.SlidingExpiration = slidingExpiration;

            options.LoginPath = "/Account/Login";
            // LogoutPath is intentionally NOT set. The actual logout
            // endpoint now lives at /Account/SignOut (renamed from
            // /Account/Logout — see Program.cs LOGOUT ENDPOINT section
            // for the full investigation history of the
            // AmbiguousMatchException that prompted the rename).
            //
            // Even though the endpoint is no longer at /Account/Logout,
            // we STILL leave LogoutPath empty: setting it to ANY value
            // (whether "/Account/Logout" or "/Account/SignOut") causes
            // .NET 10's cookie auth handler to register an implicit
            // endpoint at that path during endpoint routing, which
            // conflicts with our explicit MapGet/MapPost endpoints and
            // throws AmbiguousMatchException when the logout button is
            // clicked.
            //
            // SignOutAsync always clears the cookie regardless of
            // LogoutPath — that option only controls the handler's
            // AUTO-REDIRECT behavior, which we don't want (our endpoint
            // issues its own Results.Redirect).
            // options.LogoutPath = "/Account/SignOut";
            options.AccessDeniedPath = "/Account/AccessDenied";

            // ReturnUrlParameter — the query string key that the cookie
            // middleware watches for on the login page. After successful
            // sign-in, SignInManager redirect to this URL. We use the
            // standard ASP.NET Core default ("ReturnUrl") so the
            // RedirectToLogin.razor component's existing
            // `?returnUrl=...` query string matches.
            options.ReturnUrlParameter = "returnUrl";
        });

        // ------------------------------------------------------------------
        // 4. Repository registrations.
        //
        //    All Scoped — they depend on the scoped ApplicationDbContext.
        //    Each handler invocation gets a fresh DbContext + fresh repository
        //    instances; they're disposed together at the end of the scope.
        // ------------------------------------------------------------------
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ISaleRepository, SaleRepository>();
        services.AddScoped<IUserRepository, UserRepository>();

        // ------------------------------------------------------------------
        // 5. Application-service registrations.
        //
        //    All Scoped for the same reason as the repositories — they depend
        //    on the scoped ApplicationDbContext (and UserAccountService also
        //    depends on the scoped UserManager<ApplicationUser>).
        // ------------------------------------------------------------------
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ISaleNumberGenerator, SaleNumberGenerator>();
        services.AddScoped<IUserAccountService, UserAccountService>();

        // ------------------------------------------------------------------
        // 5c. Claims transformation — keeps FullName claim current.
        //
        //     FullNameClaimsTransformation runs on every authenticated
        //     request and enriches the ClaimsPrincipal with the current
        //     FullName from the Domain Users table (with a 30s per-user
        //     cache). This fixes the "stale cookie" bug where the admin's
        //     name sometimes showed as "ADMIN-0001" (the username fallback
        //     baked into the cookie at login time when the DomainUser
        //     lookup failed transiently).
        //
        //     AddMemoryCache provides the IMemoryCache that
        //     FullNameClaimsTransformation uses to avoid hitting the DB on
        //     every single request. See the class XML doc for details.
        // ------------------------------------------------------------------
        services.AddMemoryCache();
        services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation,
                            FullNameClaimsTransformation>();

        // ------------------------------------------------------------------
        // 5b. File storage (Phase 7 item C).
        //
        //    IFileStorage → LocalFileStorage — Scoped. Writes uploads to a
        //    configurable root directory (default wwwroot/uploads; override
        //    via FileStorage:RootPath in appsettings.json for prod so the
        //    folder survives app redeployments). Atomic writes, magic-byte
        //    content-type sniffing, max-size enforcement — see
        //    LocalFileStorage.cs for the full design rationale.
        //
        //    Scoped (not Singleton) per the IFileStorage docstring — the
        //    current impl is stateless beyond config, but Scoped is the
        //    safer default for a future impl that might need per-request
        //    state.
        // ------------------------------------------------------------------
        services.AddScoped<IFileStorage, Storage.LocalFileStorage>();

        // ------------------------------------------------------------------
        // 5. Wolverine host configuration.
        //
        //    CRITICAL (Wolverine 6.22 behavior):
        //    As of this fix, the Wolverine options (runtime compilation, SQL
        //    Server message store, durable local queues, EF Core transactional
        //    middleware, domain-event scraper) are NO LONGER applied via
        //    `services.Configure<WolverineOptions>`. That API doesn't work
        //    for Wolverine — it doesn't read its options from the
        //    IOptions<WolverineOptions> pipeline.
        //
        //    Instead, we expose a SEPARATE public static extension method
        //    `ConfigureInfrastructureWolverine(WolverineOptions opts, IConfiguration)`
        //    further down in this file. The WebUI's Program.cs invokes it
        //    as a clean extension-method call (`opts.ConfigureInfrastructureWolverine(config)`)
        //    from inside `builder.Host.UseWolverine(opts => { ... })`. This
        //    applies our Infrastructure-layer Wolverine config to the SAME
        //    options instance Wolverine actually uses.
        //
        //    Application layer does the same (its own `ConfigureApplicationWolverine`
        //    adds handler discovery + middleware + FluentValidation). The
        //    Program.cs calls both configurators inside the same UseWolverine
        //    lambda, each as a clean extension-method call on `opts`. The
        //    methods are named DISTINCTLY (Application vs Infrastructure) so
        //    they can coexist as extension methods on WolverineOptions without
        //    ambiguity -- calling them with identical names would require
        //    static-call syntax which is fragile.
        //
        //    The full design rationale for the five-piece transactional-outbox
        //    wiring (SQL Server message store + durable local queues + EF Core
        //    transactional middleware + auto-apply transactions + domain-event
        //    scraper) is documented in the `ConfigureInfrastructureWolverine`
        //    method below.
        // ------------------------------------------------------------------

        return services;
    }

    /// <summary>
    /// Applies Infrastructure-layer Wolverine configuration (runtime code
    /// generation, SQL Server message store, durable local queues, EF Core
    /// transactional middleware, domain-event scraper) to the given
    /// <paramref name="opts"/> instance. MUST be called from inside
    /// <c>builder.Host.UseWolverine(opts =&gt; { ... })</c> in the WebUI's
    /// Program.cs -- Wolverine does NOT read its options from the
    /// <c>IOptions&lt;WolverineOptions&gt;</c> pipeline, so
    /// <c>services.Configure&lt;WolverineOptions&gt;</c> does not work.
    /// </summary>
    /// <remarks>
    /// NAMING: This method is deliberately named <c>ConfigureInfrastructureWolverine</c>
    /// (not <c>ConfigureWolverine</c>) so it can coexist with the Application
    /// layer's <c>ConfigureApplicationWolverine</c> extension method on the
    /// same <c>WolverineOptions</c> type without ambiguity. With identical
    /// names the C# compiler requires static-call syntax to disambiguate
    /// (<c>Class.Method(opts, config)</c>), which is fragile and produces
    /// confusing "No overload for method 'ConfigureWolverine' takes 2 arguments"
    /// errors when one of the two files is missed during a refactor. Distinct
    /// names let the caller use clean extension-method syntax:
    /// <code>
    /// builder.Host.UseWolverine(opts =&gt;
    /// {
    ///     opts.ConfigureApplicationWolverine(builder.Configuration);
    ///     opts.ConfigureInfrastructureWolverine(builder.Configuration);
    /// });
    /// </code>
    /// </remarks>
    /// <param name="opts">
    /// The <see cref="WolverineOptions"/> instance provided by Wolverine's
    /// <c>Host.UseWolverine(opts =&gt; ...)</c> lambda. Mutated in place.
    /// </param>
    /// <param name="configuration">
    /// Application configuration. Used to read the database connection string
    /// (for the SQL Server message store) from
    /// <c>TakOne:Database:ConnectionString</c>.
    /// </param>
    public static void ConfigureInfrastructureWolverine(
        this WolverineOptions opts,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(configuration);

        // ------------------------------------------------------------------
        // Re-parse TakOneDatabaseOptions from configuration.
        //
        // The previous implementation parsed this ONCE in AddTakOneInfrastructure
        // and captured it in a local `databaseOptions` variable. Since the
        // Wolverine config now lives in a SEPARATE method (this one), that
        // local is out of scope and we re-parse it here. Cheap operation,
        // happens only once at startup.
        //
        // SECURITY: this local variable captures the connection string. It is
        // passed to opts.PersistMessagesWithSqlServer(...) and never leaves
        // this method. It is never logged, never exposed as a method parameter,
        // never stored in a static field.
        // ------------------------------------------------------------------
        var databaseOptions = new TakOneDatabaseOptions
        {
            ConnectionString = configuration
                .GetSection(TakOneDatabaseOptions.SectionName)
                .GetValue<string>(nameof(TakOneDatabaseOptions.ConnectionString))
                ?? string.Empty
        };
        databaseOptions.EnsureValid();

        // ------------------------------------------------------------------
        // (0) Runtime code generation — Wolverine 6.x removed the Roslyn
        //     runtime compiler from core WolverineFx and split it into the
        //     WolverineFx.RuntimeCompilation NuGet package. Without this
        //     call (or the package's auto-registration), Wolverine throws
        //     at startup:
        //       "Wolverine is running in TypeLoadMode.Dynamic, which
        //        compiles handler/middleware code at runtime, but no
        //        IAssemblyGenerator (Roslyn) is registered."
        //
        //     TypeLoadMode.Dynamic is Wolverine's default in 6.x, so we
        //     don't set it explicitly — we just enable the runtime
        //     compiler that Dynamic mode requires.
        //
        //     PRODUCTION HARDENING (future):
        //       For production, remove this call AND the
        //       WolverineFx.RuntimeCompilation package, then pre-generate
        //       handler/middleware code with `dotnet run -- codegen write`
        //       and set opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static.
        //       That removes the Roslyn runtime dependency (~10 MB) and
        //       speeds up startup. See https://wolverinefx.net/guide/codegen.html.
        // ------------------------------------------------------------------
        opts.UseRuntimeCompilation();

        // ------------------------------------------------------------------
        // (0b) Surgical service-location opt-in for ASP.NET Identity's
        //      UserManager<ApplicationUser>.
        //
        // WHY THIS IS REQUIRED — Wolverine 6.x codegen + Identity:
        //   Wolverine 6.0 changed the default ServiceLocationPolicy from
        //   AllowedButWarn to NotAllowed. When Wolverine JIT-compiles a
        //   message handler, its codegen walks the constructor dependency
        //   graph of every service the handler needs, attempting to INLINE
        //   each construction (emit `new TImpl(...)` instead of
        //   `sp.GetRequiredService<T>()`).
        //
        //   For handlers that touch IUserAccountService, the chain is:
        //
        //     GetUserByIdQueryHandler
        //       → IUserAccountService  = UserAccountService (Scoped)
        //         → UserManager<ApplicationUser> (Scoped)
        //           → IServiceProvider   ← Wolverine refuses to inline this
        //
        //   `UserManager<TUser>` is a SEALED Microsoft class. Its constructor
        //   signature takes `IServiceProvider` directly (Microsoft uses it
        //   internally to lazily resolve IUserStore<T>, IPasswordHasher<T>,
        //   the validators, etc.). We cannot change this — it's framework
        //   code, not ours.
        //
        //   When Wolverine's codegen reaches the IServiceProvider parameter
        //   of UserManager's constructor, it considers that "service
        //   location" (an anti-pattern) and — under NotAllowed — REFUSES to
        //   generate the handler. The host throws at the first attempt to
        //   dispatch GetUserByIdQuery:
        //
        //     fail: Wolverine.Runtime.WolverineRuntime[0]
        //       Failed to create a message handler for
        //       TakOne.Application.Users.Queries.GetUserById.GetUserByIdQuery
        //     Wolverine.Configuration.InvalidServiceLocationException:
        //       Found service locations while generating code for Message
        //       Handler for ... but ServiceLocationPolicy.NotAllowed is in
        //       effect ...
        //       Service TakOne.Application.Common.Interfaces.IUserAccountService:
        //         Dependency: UserManager<ApplicationUser>
        //         Dependency: System.IServiceProvider
        //         Your code is directly using IServiceProvider
        //
        //   Note: this is a FALSE POSITIVE. The IServiceProvider usage is
        //   inside Microsoft's UserManager<T>, not in our code. But
        //   Wolverine can't tell the difference — it just sees the
        //   dependency chain.
        //
        // WHY THE SURGICAL OPT-IN (not the global one):
        //   Wolverine's docs (https://wolverinefx.net/guide/codegen.html)
        //   recommend the surgical API:
        //
        //     opts.CodeGeneration.AlwaysUseServiceLocationFor<TService>();
        //
        //   This tells Wolverine: "whenever you encounter a constructor
        //   dependency on TService, emit `sp.GetRequiredService<TService>()`
        //   instead of trying to inline-construct it." It is the MINIMAL
        //   opt-in — only the named type is resolved via the service
        //   locator; every other dependency stays constructor-inlined.
        //
        //   The alternative (`opts.ServiceLocationPolicy =
        //   ServiceLocationPolicy.AlwaysAllowed`) would disable the check
        //   globally, masking any FUTURE genuine service-location
        //   anti-patterns we might accidentally introduce. The surgical
        //   API keeps Wolverine's safety net intact for everything else.
        //
        // WHY THIS LIVES IN INFRASTRUCTURE (not Application):
        //   - UserManager<T> and ApplicationUser are Infrastructure-layer
        //     types. The Application layer has no knowledge of them.
        //   - This is a Wolverine FRAMEWORK-GLUE concern (compensating for
        //     a quirk of ASP.NET Identity), not an Application-layer
        //     concern. The Application layer's ConfigureApplicationWolverine
        //     only wires engine-agnostic things (handler discovery,
        //     middleware, FluentValidation).
        //   - Co-located with `opts.UseRuntimeCompilation()` and the SQL
        //     Server message store — both also framework/engine glue.
        //
        // SCOPE OF IMPACT:
        //   Affects every handler that takes IUserAccountService as a
        //   constructor parameter (currently: GetUserByIdQueryHandler,
        //   CreateStaffCommandHandler, CreateCustomerCommandHandler,
        //   AssignUserRoleCommandHandler, RemoveUserRoleCommandHandler,
        //   ResetUserPasswordCommandHandler). Without this opt-in, ALL of
        //   those handlers fail to JIT-compile and the pages that call them
        //   (UserDetail, CreateUser, AdminUsers, ForgotPassword,
        //   ResetPassword) throw at runtime.
        // ------------------------------------------------------------------
        opts.CodeGeneration.AlwaysUseServiceLocationFor<UserManager<ApplicationUser>>();

        // ------------------------------------------------------------------
        // (a) SQL Server message store — durably persists outgoing messages
        //     until processed. Uses the same connection string as
        //     ApplicationDbContext (re-parsed from configuration above).
        // ------------------------------------------------------------------
        opts.PersistMessagesWithSqlServer(databaseOptions.ConnectionString);

        // ------------------------------------------------------------------
        // (b) Durable local queues — enrolls ALL local queues into durable
        //     inbox/outbox processing. Without this, messages are stored
        //     in-memory only and lost on process restart.
        // ------------------------------------------------------------------
        opts.Policies.UseDurableLocalQueues();

        // ------------------------------------------------------------------
        // (c) Register EF Core as the transaction provider.
        // ------------------------------------------------------------------
        opts.UseEntityFrameworkCoreTransactions();

        // ------------------------------------------------------------------
        // (d) Auto-apply transactional middleware to all handlers using
        //     a DbContext. Individual handlers can opt out with
        //     [NonTransactional] (namespace Wolverine.Attributes).
        // ------------------------------------------------------------------
        opts.Policies.AutoApplyTransactions();

        // ------------------------------------------------------------------
        // (e) Scrape domain events from AggregateRoot entities at commit
        //     time and publish them through the enrolled outbox.
        //
        //     Generic args:
        //       - TEntityType = AggregateRoot — the scraper uses
        //         OfType<TEntityType>() to filter, so non-aggregate
        //         entities (SaleLineItem, etc.) are silently skipped.
        //       - TDomainEvent = BaseDomainEvent — our custom abstract
        //         base class. Wolverine does NOT require events to
        //         implement any marker interface; TDomainEvent can be
        //         any type.
        //
        //     Delegate: agg => agg.DomainEvents — our property name.
        //     (Wolverine's docs sample uses "Events", but the generic
        //     overload accepts any delegate returning IEnumerable<TEvent>.)
        // ------------------------------------------------------------------
        opts.PublishDomainEventsFromEntityFrameworkCore<AggregateRoot, BaseDomainEvent>
            (agg => agg.DomainEvents);
    }
}
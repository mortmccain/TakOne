using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TakOne.Application.Common.Middlewares;
using Wolverine;
using Wolverine.FluentValidation;

namespace TakOne.Application.DependencyInjection;

/// <summary>
/// Composition root for the TakOne Application layer.
///
/// This is the ONLY extension method the WebUI's Program.cs needs to call to
/// wire up everything the Application layer provides:
///
///   - Wolverine command/query bus (with discovery of all handlers in this assembly)
///   - FluentValidation integration (validators run automatically before handlers)
///   - Wolverine middleware pipeline (logging → performance → authorization → domain-exception)
///
///     Engine-specific concerns (SQL Server message store, durable local
///     queues, EF Core transactional middleware, domain-event scraper) are
///     registered by <c>AddTakOneInfrastructure</c> in the Infrastructure layer.
///
/// WHAT IS NOT REGISTERED HERE (deferred to Infrastructure):
///   - <see cref="TakOne.Application.Common.Interfaces.ICurrentUserService"/> — the
///     real implementation depends on IHttpContextAccessor (an ASP.NET Core
///     abstraction), so it lives in the WebUI layer and is registered there.
///   - Repository implementations (IProductRepository, ICategoryRepository, etc.)
///   - IUnitOfWork
///   - ISaleNumberGenerator
///   - IUserAccountService
///   - The EF Core DbContext
///   - ASP.NET Identity
///   - Wolverine SQL Server message store (<c>opts.PersistMessagesWithSqlServer(...)</c>)
///     — engine-specific, lives in Infrastructure.
///   - Wolverine durable local queues policy (<c>opts.Policies.UseDurableLocalQueues()</c>)
///     — only meaningful alongside a message store, so co-located with it in
///     Infrastructure.
///   - EF Core transactional middleware (<c>opts.UseEntityFrameworkCoreTransactions()</c>)
///     — needs the DbContext registered first.
///   - Domain-event scraper (<c>opts.PublishDomainEventsFromEntityFrameworkCore</c>)
///     — same reason.
///
///   The Infrastructure- and WebUI-layer registrations are performed by their
///   own extension methods, which the WebUI calls AFTER this one. Keeping
///   them separate honors the dependency direction: Application never
///   references Infrastructure or WebUI, only the other way around.
///
/// ENGINE-AGNOSTIC BY DESIGN:
///   This method does NOT reference any specific database engine, message-bus
///   transport, or persistence technology. The Application layer's Wolverine
///   configuration is limited to:
///     - Handler discovery (scans this assembly for <c>HandleAsync</c> methods)
///     - Middleware pipeline ordering (logging → perf → auth → domain-exception)
///     - FluentValidation integration
///   Everything that touches a concrete engine (SQL Server message store,
///   EF Core transactional middleware, durable-queues policy) is owned by
///   <c>AddTakOneInfrastructure</c>. This keeps <c>TakOne.Application.csproj</c>
///   free of any <c>WolverineFx.SqlServer</c> / <c>WolverineFx.EntityFrameworkCore</c>
///   / <c>Microsoft.EntityFrameworkCore.*</c> references.
///
/// CONNECTION STRING HANDLING:
///   This method takes <c>IConfiguration</c> (not a connection string) and
///   does NOT read the connection string at all — that's now exclusively
///   <c>AddTakOneInfrastructure</c>'s job, since it's the only layer that
///   needs it (for the DbContext and the Wolverine message store). The
///   connection string never appears in any method signature, log, or error
///   message anywhere in the codebase.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Application-layer services to the DI container and configures
    /// Wolverine for command/query dispatch, validation, and middleware.
    /// </summary>
    /// <param name="services">
    /// The DI container being configured. Typically <c>builder.Services</c>
    /// in Program.cs.
    /// </param>
    /// <param name="configuration">
    /// Application configuration. Used here ONLY for the optional
    /// <c>Wolverine:SlowRequestThresholdMs</c> tuning knob. The database
    /// connection string is NOT read by this method — it's read by
    /// <c>AddTakOneInfrastructure</c> (which needs it for the DbContext and
    /// the Wolverine SQL Server message store). The connection string never
    /// appears as a method parameter anywhere in the codebase.
    /// </param>
    /// <returns>
    /// The modified <see cref="IServiceCollection"/> (so callers can chain
    /// further DI calls if they wish).
    /// </returns>
    public static IServiceCollection AddTakOneApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ------------------------------------------------------------------
        // 0. Argument guards. Cheap, but they turn "why is Wolverine empty?"
        //     into an immediate, actionable exception at startup.
        // ------------------------------------------------------------------
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ------------------------------------------------------------------
        // 1. FluentValidation — register all validators in this assembly.
        //
        //    We register them EXPLICITLY here (rather than letting Wolverine's
        //    FluentValidation integration auto-discover them) for two reasons:
        //      a) It lets us resolve validators directly via DI for non-Wolverine
        //         callers (e.g. a controller that wants to call
        //         `validator.ValidateAsync(...)` manually).
        //      b) It gives us control over the lifetime (Transient — validators
        //         are stateless so Transient is cheapest).
        //
        //    Because we're registering them ourselves, we MUST tell Wolverine
        //    NOT to also auto-discover them, otherwise we'd hit the docs'
        //    "double registration" warning. We do that in section 3c by
        //    passing `RegistrationBehavior.ExplicitRegistration` to
        //    `UseFluentValidation(...)`.
        // ------------------------------------------------------------------
        services.AddValidatorsFromAssembly(
            Assembly.GetExecutingAssembly(),
            ServiceLifetime.Transient);

        // ------------------------------------------------------------------
        // 2. Optional Wolverine tuning from configuration. We read it here
        //    (at the IServiceCollection level) so the value applies BEFORE
        //    the Wolverine host starts.
        //
        //    We default to 500 ms (a common enterprise "perceptible latency"
        //    threshold) and let ops override via appsettings.json:
        //
        //       "Wolverine": { "SlowRequestThresholdMs": 300 }
        // ------------------------------------------------------------------
        var slowThresholdMs = configuration.GetValue<int?>("Wolverine:SlowRequestThresholdMs");
        if (slowThresholdMs.HasValue && slowThresholdMs.Value > 0)
        {
            PerformanceMiddleware.SlowRequestThresholdMs = slowThresholdMs.Value;
        }

        // ------------------------------------------------------------------
        // 3. Wolverine host configuration.
        //
        //    Wolverine's recommended pattern is to configure the bus on the
        //    IHostBuilder (builder.Host.UseWolverine(opts => { ... })). We
        //    want the Application layer to own that config (so the WebUI's
        //    Program.cs stays clean), so we use IServiceCollection.Configure
        //    <WolverineOptions>(...) here. The WebUI then calls
        //    builder.Host.UseWolverine() with a no-op lambda, which triggers
        //    Wolverine to apply the configured options.
        //
        //    The WebUI's Program.cs would look like:
        //
        //       builder.Services.AddTakOneApplication(builder.Configuration);
        //
        //       builder.Host.UseWolverine(opts => { /* opts already configured */ });
        //
        //    NOTE: Infrastructure's AddTakOneInfrastructure(...) extension method
        //    (Step 7) will add ANOTHER services.Configure<WolverineOptions>(...)
        //    lambda to register EF Core transactional middleware + the
        //    domain-event scraper. ASP.NET Core composes multiple Configure<T>
        //    lambdas in registration order, so Infrastructure's additions layer
        //    on top of ours cleanly.
        // ------------------------------------------------------------------
        services.Configure<WolverineOptions>(opts =>
        {
            // --------------------------------------------------------------
            // 3a. Discover handlers in THIS assembly. Wolverine's source
            //     generator scans for classes with the conventional
            //     `public static async Task<...> HandleAsync(...)` method
            //     pattern. By restricting to this assembly we avoid picking
            //     up handlers from referenced assemblies by accident.
            // --------------------------------------------------------------
            opts.Discovery.IncludeAssembly(typeof(ServiceCollectionExtensions).Assembly);

            // --------------------------------------------------------------
            // 3b. MIDDLEWARE PIPELINE.
            //
            //     Wolverine middleware is registered via
            //     `opts.Policies.AddMiddleware<T>()` (NOT `opts.Policies.Add<T>()`,
            //     which is for IWolverinePolicy classes — a different concept).
            //
            //     Order matters — Wolverine applies middleware in registration
            //     order. The pipeline we want for every message is:
            //
            //        1. LoggingMiddleware.BeforeAsync         (logs "Starting X")
            //        2. PerformanceMiddleware.BeforeAsync     (starts stopwatch)
            //        3. AuthorizationMiddleware.Before        (rejects unauth'd
            //                                                   calls before the
            //                                                   handler runs;
            //                                                   short-circuits)
            //        4. — handler runs —
            //        5. DomainExceptionMiddleware.Handle      (catches any
            //                                                   DomainException
            //                                                   thrown by the
            //                                                   handler or
            //                                                   aggregate and
            //                                                   converts to
            //                                                   Result.Failure)
            //        6. PerformanceMiddleware.AfterAsync      (logs if slow)
            //        7. LoggingMiddleware.AfterAsync          (logs "Completed X")
            //
            //     NOTE on AuthorizationMiddleware short-circuit: when Before
            //     returns a non-null Result, Wolverine uses it as the handler's
            //     return value and SKIPS the handler invocation. The
            //     PerformanceMiddleware.AfterAsync and LoggingMiddleware.
            //     AfterAsync methods still run — which is what we want, so the
            //     "Completed X" log always pairs with a "Starting X" log.
            //
            //     MIDDLEWARE CONVENTION:
            //     Wolverine recognizes Before/BeforeAsync/After/AfterAsync/Load/
            //     LoadAsync/Validate/ValidateAsync/Finally/FinallyAsync method
            //     names by case-sensitive convention — NO attribute or interface
            //     is required on the middleware class itself.
            // --------------------------------------------------------------
            opts.Policies.AddMiddleware<LoggingMiddleware>();
            opts.Policies.AddMiddleware<PerformanceMiddleware>();
            opts.Policies.AddMiddleware<AuthorizationMiddleware>();
            opts.Policies.AddMiddleware<DomainExceptionMiddleware>();

            // --------------------------------------------------------------
            // 3c. FLUENTVALIDATION INTEGRATION.
            //
            //     `UseFluentValidation(RegistrationBehavior.ExplicitRegistration)`
            //     tells Wolverine: "validators are already registered in DI —
            //     don't auto-discover them again". This pairs with the
            //     `services.AddValidatorsFromAssembly(...)` call in section 1
            //     above and avoids the docs' "double registration" warning.
            //
            //     Wolverine will resolve the matching AbstractValidator<TCommand>
            //     from DI and run it BEFORE the handler. If validation fails,
            //     Wolverine short-circuits with the validation failures as the
            //     result — the handler never runs.
            //
            //     Reference: https://wolverinefx.net/guide/handlers/fluent-validation
            // --------------------------------------------------------------
            opts.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);

            // --------------------------------------------------------------
            // 3d. (MOVED TO INFRASTRUCTURE)
            //
            //     The SQL Server message store (<c>PersistMessagesWithSqlServer</c>)
            //     and the durable-local-queues policy (<c>UseDurableLocalQueues</c>)
            //     used to live here but have been MOVED to <c>AddTakOneInfrastructure</c>.
            //     Both are SQL Server / persistence-specific concerns that don't
            //     belong in the Application layer. Co-locating them with the EF
            //     Core transactional middleware in Infrastructure makes the
            //     "SQL Server durability setup" a single cohesive block, and
            //     lets this layer drop its <c>WolverineFx.SqlServer</c>
            //     dependency entirely.
            //
            //     The full transactional-outbox chain (message store + durable
            //     queues + EF Core tx middleware + domain-event scraper) is
            //     documented in <c>AddTakOneInfrastructure</c>.
            // --------------------------------------------------------------
        });

        return services;
    }
}
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
///   - Wolverine middleware pipeline (logging -> performance -> authorization -> domain-exception)
///
///     Engine-specific concerns (SQL Server message store, durable local
///     queues, EF Core transactional middleware, domain-event scraper) are
///     registered by <c>AddTakOneInfrastructure</c> in the Infrastructure layer.
///
/// WHAT IS NOT REGISTERED HERE (deferred to Infrastructure):
///   - <see cref="TakOne.Application.Common.Interfaces.ICurrentUserService"/> -- the
///     real implementation depends on IHttpContextAccessor (an ASP.NET Core
///     abstraction), so it lives in the WebUI layer and is registered there.
///   - Repository implementations (IProductRepository, ICategoryRepository, etc.)
///   - IUnitOfWork
///   - ISaleNumberGenerator
///   - IUserAccountService
///   - The EF Core DbContext
///   - ASP.NET Identity
///   - Wolverine SQL Server message store (<c>opts.PersistMessagesWithSqlServer(...)</c>)
///     -- engine-specific, lives in Infrastructure.
///   - Wolverine durable local queues policy (<c>opts.Policies.UseDurableLocalQueues()</c>)
///     -- only meaningful alongside a message store, so co-located with it in
///     Infrastructure.
///   - EF Core transactional middleware (<c>opts.UseEntityFrameworkCoreTransactions()</c>)
///     -- needs the DbContext registered first.
///   - Domain-event scraper (<c>opts.PublishDomainEventsFromEntityFrameworkCore</c>)
///     -- same reason.
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
///     - Middleware pipeline ordering (logging -> perf -> auth -> domain-exception)
///     - FluentValidation integration
///   Everything that touches a concrete engine (SQL Server message store,
///   EF Core transactional middleware, durable-queues policy) is owned by
///   <c>AddTakOneInfrastructure</c>. This keeps <c>TakOne.Application.csproj</c>
///   free of any <c>WolverineFx.SqlServer</c> / <c>WolverineFx.EntityFrameworkCore</c>
///   / <c>Microsoft.EntityFrameworkCore.*</c> references.
///
/// CONNECTION STRING HANDLING:
///   This method takes <c>IConfiguration</c> (not a connection string) and
///   does NOT read the connection string at all -- that's now exclusively
///   <c>AddTakOneInfrastructure</c>'s job, since it's the only layer that
///   needs it (for the DbContext and the Wolverine message store). The
///   connection string never appears in any method signature, log, or error
///   message anywhere in the codebase.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Application-layer services to the DI container.
    /// Wolverine configuration is performed separately by
    /// <see cref="ConfigureApplicationWolverine"/>, which must be called
    /// from inside <c>builder.Host.UseWolverine(opts =&gt; { ... })</c> in
    /// the WebUI's Program.cs.
    /// </summary>
    /// <param name="services">
    /// The DI container being configured. Typically <c>builder.Services</c>
    /// in Program.cs.
    /// </param>
    /// <param name="configuration">
    /// Application configuration. Used here ONLY for the optional
    /// <c>Wolverine:SlowRequestThresholdMs</c> tuning knob. The database
    /// connection string is NOT read by this method -- it's read by
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
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // ------------------------------------------------------------------
        // 1. FluentValidation -- register all validators in this assembly.
        //
        //    We register them EXPLICITLY here (rather than letting Wolverine's
        //    FluentValidation integration auto-discover them) for two reasons:
        //      a) It lets us resolve validators directly via DI for non-Wolverine
        //         callers (e.g. a controller that wants to call
        //         `validator.ValidateAsync(...)` manually).
        //      b) It gives us control over the lifetime (Transient -- validators
        //         are stateless so Transient is cheapest).
        //
        //    Because we're registering them ourselves, we MUST tell Wolverine
        //    NOT to also auto-discover them, otherwise we'd hit the docs'
        //    "double registration" warning. We do that in
        //    ConfigureApplicationWolverine by passing
        //    `RegistrationBehavior.ExplicitRegistration` to `UseFluentValidation(...)`.
        // ------------------------------------------------------------------
        services.AddValidatorsFromAssembly(
            Assembly.GetExecutingAssembly(),
            ServiceLifetime.Transient);

        // ------------------------------------------------------------------
        // 2. Optional Wolverine tuning from configuration. We read it here
        //    (at the IServiceCollection level) so the value applies BEFORE
        //    the Wolverine host starts. ConfigureApplicationWolverine also
        //    sets this -- both paths are harmless.
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

        return services;
    }

    /// <summary>
    /// Applies Application-layer Wolverine configuration (handler discovery,
    /// middleware pipeline, FluentValidation integration) to the given
    /// <paramref name="opts"/> instance. MUST be called from inside
    /// <c>builder.Host.UseWolverine(opts =&gt; { ... })</c> in the WebUI's
    /// Program.cs -- Wolverine does NOT read its options from the
    /// <c>IOptions&lt;WolverineOptions&gt;</c> pipeline, so
    /// <c>services.Configure&lt;WolverineOptions&gt;</c> does not work.
    /// </summary>
    /// <remarks>
    /// WHY THIS METHOD EXISTS (and why <c>AddTakOneApplication</c> no longer
    /// touches <c>WolverineOptions</c>):
    ///
    /// The PREVIOUS implementation used <c>services.Configure&lt;WolverineOptions&gt;</c>
    /// inside <c>AddTakOneApplication</c> to register Application-layer
    /// Wolverine config (handler discovery, middleware pipeline, FluentValidation).
    /// The WebUI's Program.cs then called <c>builder.Host.UseWolverine()</c>
    /// (parameterless) expecting the IOptions pipeline to apply our config.
    ///
    /// That does NOT work. Wolverine 6.x does NOT read its options from the
    /// <c>IOptions&lt;WolverineOptions&gt;</c> pipeline. The parameterless
    /// <c>UseWolverine()</c> overload creates a default <c>WolverineOptions</c>
    /// instance and uses that directly; the
    /// <c>services.Configure&lt;WolverineOptions&gt;</c> lambdas registered by
    /// <c>AddTakOneApplication</c> and <c>AddTakOneInfrastructure</c> are
    /// SILENTLY IGNORED.
    ///
    /// SYMPTOM of the broken approach:
    ///   <code>
    ///     warn: Wolverine found no handlers. If this is unexpected,
    ///           check the assemblies that it's scanning.
    ///     Searching assembly Wolverine.RuntimeCompilation
    ///     Searching assembly TakOne.WebUI
    ///     (NOT searching TakOne.Application -- even though we'd called
    ///      opts.Discovery.IncludeAssembly(typeof(ServiceCollectionExtensions).Assembly))
    ///
    ///     fail: Failed to create a message handler for
    ///           TakOne.Application.Dashboard.Queries.GetDashboardStatsQuery
    ///     IndeterminateRoutesException: Could not determine any valid
    ///     subscribers or local handlers for message type ...
    ///   </code>
    ///
    /// FIX:
    ///   We expose a SEPARATE public static extension method
    ///   <c>ConfigureApplicationWolverine(WolverineOptions, IConfiguration)</c>
    ///   that the WebUI's Program.cs invokes as a clean extension-method call
    ///   (<c>opts.ConfigureApplicationWolverine(config)</c>) from inside
    ///   <c>builder.Host.UseWolverine(opts =&gt; { ... })</c>. This guarantees
    ///   our config is applied to the SAME options instance Wolverine actually
    ///   uses.
    ///
    ///   The Infrastructure layer exposes its own
    ///   <c>ConfigureInfrastructureWolverine</c> (adds runtime compilation,
    ///   SQL Server message store, EF Core transactional middleware, and the
    ///   domain-event scraper). The Program.cs calls both configurators inside
    ///   the same UseWolverine lambda, each as a clean extension-method call
    ///   on <c>opts</c>.
    ///
    /// NAMING RATIONALE:
    ///   The methods are named DISTINCTLY (ConfigureApplicationWolverine vs
    ///   ConfigureInfrastructureWolverine) so they can coexist as extension
    ///   methods on <c>WolverineOptions</c> without ambiguity. Calling them
    ///   with identical names would require static-call syntax
    ///   (<c>Class.Method(opts, config)</c>) which is fragile and prone to
    ///   "No overload takes 2 arguments" errors when one file is missed
    ///   during a refactor. Distinct names let the caller use clean
    ///   extension-method syntax:
    ///   <code>
    ///     builder.Host.UseWolverine(opts =&gt;
    ///     {
    ///         opts.ConfigureApplicationWolverine(builder.Configuration);
    ///         opts.ConfigureInfrastructureWolverine(builder.Configuration);
    ///     });
    ///   </code>
    /// </remarks>
    /// <param name="opts">
    /// The <see cref="WolverineOptions"/> instance provided by Wolverine's
    /// <c>Host.UseWolverine(opts =&gt; ...)</c> lambda. Mutated in place.
    /// </param>
    /// <param name="configuration">
    /// Application configuration. Used here ONLY for the optional
    /// <c>Wolverine:SlowRequestThresholdMs</c> tuning knob.
    /// </param>
    public static void ConfigureApplicationWolverine(
        this WolverineOptions opts,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(configuration);

        // Optional slow-request threshold from configuration. (We also set it
        // via the static property in AddTakOneApplication above -- doing it
        // here too is harmless and keeps the configurator self-contained if a
        // future caller uses only ConfigureApplicationWolverine.)
        var slowThresholdMs = configuration.GetValue<int?>("Wolverine:SlowRequestThresholdMs");
        if (slowThresholdMs.HasValue && slowThresholdMs.Value > 0)
        {
            PerformanceMiddleware.SlowRequestThresholdMs = slowThresholdMs.Value;
        }

        // --------------------------------------------------------------
        // (a) Discover handlers in THIS assembly (TakOne.Application).
        //     Wolverine's source generator scans for classes with the
        //     conventional `public static async Task<...> HandleAsync(...)`
        //     method pattern. By restricting to this assembly we avoid
        //     picking up handlers from referenced assemblies by accident.
        //
        //     Without this call, Wolverine ONLY scans the entry assembly
        //     (TakOne.WebUI), which has NO handlers -- all our command/query
        //     handlers live in TakOne.Application. That's exactly the bug
        //     that produced the "Wolverine found no handlers" warning.
        // --------------------------------------------------------------
        opts.Discovery.IncludeAssembly(typeof(ServiceCollectionExtensions).Assembly);

        // --------------------------------------------------------------
        // (b) MIDDLEWARE PIPELINE.
        //
        //     Wolverine middleware is registered via
        //     `opts.Policies.AddMiddleware<T>()` (NOT `opts.Policies.Add<T>()`,
        //     which is for IWolverinePolicy classes -- a different concept).
        //
        //     Order matters -- Wolverine applies middleware in registration
        //     order. The pipeline we want for every message is:
        //
        //        1. LoggingMiddleware.BeforeAsync         (logs "Starting X")
        //        2. PerformanceMiddleware.BeforeAsync     (starts stopwatch)
        //        3. AuthorizationMiddleware.Before        (rejects unauth'd
        //                                                   calls before the
        //                                                   handler runs;
        //                                                   short-circuits)
        //        4. -- handler runs --
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
        //     AfterAsync methods still run -- which is what we want, so the
        //     "Completed X" log always pairs with a "Starting X" log.
        //
        //     MIDDLEWARE CONVENTION:
        //     Wolverine recognizes Before/BeforeAsync/After/AfterAsync/Load/
        //     LoadAsync/Validate/ValidateAsync/Finally/FinallyAsync method
        //     names by case-sensitive convention -- NO attribute or interface
        //     is required on the middleware class itself.
        // --------------------------------------------------------------
        opts.Policies.AddMiddleware<LoggingMiddleware>();
        opts.Policies.AddMiddleware<PerformanceMiddleware>();
        opts.Policies.AddMiddleware<AuthorizationMiddleware>();

        // NOTE: DomainExceptionMiddleware was DELETED. In Wolverine 6.x,
        // middleware methods must be named
        // Before/BeforeAsync/After/AfterAsync/Finally/FinallyAsync, and
        // Finally/FinallyAsync can receive an Exception but CANNOT return
        // a value to replace the handler's output. That means we cannot
        // do "catch DomainException -> return Result.Failure(message)"
        // in middleware the way the original design intended.
        //
        // Worse: Wolverine 6.22 AUTO-DISCOVERS middleware classes by
        // convention -- any public class in a scanned assembly with a
        // method matching Before/BeforeAsync/After/AfterAsync/Finally/
        // FinallyAsync gets auto-applied to ALL handlers, EVEN IF it is
        // not registered via AddMiddleware<T>(). The only way to prevent
        // auto-discovery is to delete the class (or rename its methods to
        // not match the convention). We chose to delete it.
        //
        // Handlers that call aggregate methods which may throw
        // DomainException (e.g. sale.AddLineItem) should wrap those calls
        // in a try/catch and return Result.Failure themselves. This is
        // the recommended Wolverine 6.x pattern for exception-to-Result
        // conversion -- see https://wolverinefx.net/guide/handlers/error-handling.

        // --------------------------------------------------------------
        // (c) FLUENTVALIDATION INTEGRATION.
        //
        //     `UseFluentValidation(RegistrationBehavior.ExplicitRegistration)`
        //     tells Wolverine: "validators are already registered in DI --
        //     don't auto-discover them again". This pairs with the
        //     `services.AddValidatorsFromAssembly(...)` call in
        //     AddTakOneApplication above and avoids the docs'
        //     "double registration" warning.
        //
        //     Wolverine will resolve the matching AbstractValidator<TCommand>
        //     from DI and run it BEFORE the handler. If validation fails,
        //     Wolverine short-circuits with the validation failures as the
        //     result -- the handler never runs.
        //
        //     Reference: https://wolverinefx.net/guide/handlers/fluent-validation
        // --------------------------------------------------------------
        opts.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);

        // --------------------------------------------------------------
        // (d) (MOVED TO INFRASTRUCTURE)
        //
        //     The SQL Server message store (<c>PersistMessagesWithSqlServer</c>)
        //     and the durable-local-queues policy (<c>UseDurableLocalQueues</c>)
        //     used to live here but have been MOVED to
        //     <c>TakOne.Infrastructure.DependencyInjection.ServiceCollectionExtensions.
        //     ConfigureInfrastructureWolverine</c>. Both are SQL Server /
        //     persistence-specific concerns that don't belong in the
        //     Application layer.
        // --------------------------------------------------------------
    }
}
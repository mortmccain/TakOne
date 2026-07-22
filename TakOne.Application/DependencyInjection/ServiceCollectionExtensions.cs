using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TakOne.Application.Common.Middlewares;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.SqlServer;

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
///   - Wolverine SQL Server message store + durable local queues (for transactional
///     outbox support — the EF Core tx middleware itself is added in Step 7,
///     Infrastructure, because it needs the DbContext)
///
/// WHAT IS NOT REGISTERED HERE:
///   - <see cref="TakOne.Application.Common.Interfaces.ICurrentUserService"/> — the
///     real implementation depends on IHttpContextAccessor (an ASP.NET Core
///     abstraction), so it lives in the WebUI layer and is registered there.
///   - Repository implementations (IProductRepository, ICategoryRepository, etc.)
///   - IUnitOfWork
///   - ISaleNumberGenerator
///   - IUserAccountService
///   - The EF Core DbContext
///   - ASP.NET Identity
///   - EF Core transactional middleware (opts.UseEntityFrameworkCoreTransactions())
///     — added in Step 7 because it needs the DbContext registered first.
///
///   The Infrastructure- and WebUI-layer registrations are performed by their
///   own extension methods, which the WebUI calls AFTER this one. Keeping
///   them separate honors the dependency direction: Application never
///   references Infrastructure or WebUI, only the other way around.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all Application-layer services to the DI container and configures
    /// Wolverine for command/query dispatch, validation, middleware, and outbox.
    /// </summary>
    /// <param name="services">
    /// The DI container being configured. Typically <c>builder.Services</c>
    /// in Program.cs.
    /// </param>
    /// <param name="configuration">
    /// Application configuration. Used here to read optional Wolverine tuning
    /// knobs (e.g. slow-request threshold) — pass the root <c>IConfiguration</c>.
    /// </param>
    /// <param name="wolverineConnectionString">
    /// The SQL Server connection string Wolverine will use for its message
    /// store (the <c>wolverine_messages</c> table). Should point to the same
    /// database the application uses for its business data, so that outbox
    /// entries commit in the same transaction as business changes.
    /// </param>
    /// <returns>
    /// The modified <see cref="IServiceCollection"/> (so callers can chain
    /// further DI calls if they wish).
    /// </returns>
    public static IServiceCollection AddTakOneApplication(
        this IServiceCollection services,
        IConfiguration configuration,
        string wolverineConnectionString)
    {
        // ------------------------------------------------------------------
        // 0. Argument guards. Cheap, but they turn "why is Wolverine empty?"
        //     into an immediate, actionable exception at startup.
        // ------------------------------------------------------------------
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(wolverineConnectionString))
        {
            throw new ArgumentException
                ("Wolverine connection string must be provided so the message " + "store can be wired to a SQL Server database.",
                nameof(wolverineConnectionString));
        }

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
        services.AddValidatorsFromAssembly
            (
            Assembly.GetExecutingAssembly(),
            ServiceLifetime.Transient
            );

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
        //       builder.Services.AddTakOneApplication(
        //           builder.Configuration,
        //           builder.Configuration.GetConnectionString("DefaultConnection")!);
        //
        //       builder.Host.UseWolverine(opts => { /* opts already configured */ });
        //
        //    NOTE: Infrastructure's AddTakOneInfrastructure(...) extension method
        //    (Step 7) will add ANOTHER services.Configure<WolverineOptions>(...)
        //    lambda to register EF Core transactional middleware. ASP.NET Core
        //    composes multiple Configure<T> lambdas in registration order, so
        //    Infrastructure's additions layer on top of ours cleanly.
        // ------------------------------------------------------------------
        services.Configure<WolverineOptions>
            (
            opts =>
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
            // 3d. SQL SERVER MESSAGE STORE + DURABLE LOCAL QUEUES.
            //
            //     Two separate concerns, both required for the outbox:
            //
            //     (1) PersistMessagesWithSqlServer(connectionString) — creates
            //         the wolverine_messages table on first run and sets up
            //         the message store on the given connection string. This
            //         is where outgoing messages are durably stored until
            //         they're processed.
            //
            //     (2) UseDurableLocalQueues() — a policy that enrolls ALL
            //         local queues into durable inbox/outbox processing.
            //         Without this, messages are stored in-memory only and
            //         lost on process restart. With this, every locally
            //         dispatched message is persisted to the message store
            //         and durably delivered.
            //
            //     WHAT THIS BUYS US (combined with the EF Core transactional
            //     middleware added in Step 7):
            //       - When a handler calls IMessageBus.PublishAsync(...) inside
            //         a transaction, the published message is written to the
            //         wolverine_messages table in the SAME transaction as the
            //         business changes.
            //       - If SaveChangesAsync succeeds, both the business changes
            //         and the message are committed atomically.
            //       - If SaveChangesAsync fails, both roll back — no orphan
            //         messages, no lost events.
            //       - A background process then picks up the outbox entries
            //         and sends them to their actual destinations.
            //
            //     Reference: https://wolverinefx.net/guide/durability/sqlserver
            // --------------------------------------------------------------
            opts.PersistMessagesWithSqlServer(wolverineConnectionString);
            opts.Policies.UseDurableLocalQueues();
        });

        return services;
    }
}
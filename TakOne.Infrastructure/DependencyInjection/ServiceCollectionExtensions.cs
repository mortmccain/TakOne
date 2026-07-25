using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
///   3. <c>builder.Host.UseWolverine(_ =&gt; { })</c> — empty lambda; both
///      Application and Infrastructure have already configured
///      <c>WolverineOptions</c> via <c>services.Configure&lt;WolverineOptions&gt;</c>.
///      This call just triggers Wolverine to apply the configured options and
///      start the message bus.
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
        IConfiguration configuration)
    {
        // ------------------------------------------------------------------
        // 0. Argument guards.
        // ------------------------------------------------------------------
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

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
        // ------------------------------------------------------------------
        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            // ------------------------------------------------------------
            // Identity options — tuned for an enterprise B2B context.
            //
            // These are STARTER values. Tighten or loosen as your security
            // team requires. All can be overridden in appsettings.json by
            // moving these to IOptions<IdentityOptions> binding, but for
            // v1 hard-coding is acceptable (changing them requires a
            // restart anyway).
            // ------------------------------------------------------------

            // PASSWORD STRENGTH. B2B users don't choose their own password
            // initially (admin sets a strong one and the user changes it
            // on first login), so we don't need the most aggressive
            // policy. But we still require length 8 + 1 non-alphanumeric
            // to defeat brute-force.
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequiredLength = 8;
            options.Password.RequiredUniqueChars = 4;

            // LOCKOUT. Standard enterprise: 5 failed attempts in 15 min →
            // 15-min lockout. Prevents brute-force without locking out
            // users who fat-finger their password once.
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.AllowedForNewUsers = true;

            // USER. Disallow '@' in WorkerId (we use WorkerId as UserName).
            // Require unique emails (one account per email).
            options.User.AllowedUserNameCharacters =
                "abcdefghijklmnopqrstuvwxyz" +
                "ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
                "0123456789" +
                "-._";
            options.User.RequireUniqueEmail = true;

            // SIGN-IN. Require confirmed email before sign-in. Our
            // IUserAccountService.CreateIdentityAccountAsync sets
            // EmailConfirmed=true for admin-created accounts, so this
            // doesn't block them — but it does block any future
            // self-registration flow until the user confirms.
            options.SignIn.RequireConfirmedEmail = true;
            options.SignIn.RequireConfirmedPhoneNumber = false;
            options.SignIn.RequireConfirmedAccount = true;
        })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

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
        // 6. Wolverine SQL Server message store + durable local queues +
        //    EF Core transactional middleware + domain-event scraper.
        //
        //    This block is the COMPLETE transactional-outbox wiring. The
        //    Application layer's AddTakOneApplication configures ONLY the
        //    engine-agnostic Wolverine concerns (handler discovery, middleware
        //    pipeline, FluentValidation integration). Everything that touches
        //    a concrete engine lives here, in this single cohesive block.
        //
        //    Five cooperating pieces, applied in order:
        //
        //    (a) opts.PersistMessagesWithSqlServer(connectionString) — creates
        //        the wolverine_messages table on first run and sets up the
        //        message store on the given connection string. This is where
        //        outgoing messages are durably stored until they're processed.
        //        Uses the SAME connection string as ApplicationDbContext, so
        //        outbox entries can commit in the SAME transaction as business
        //        changes (the entire reason the outbox pattern works — split
        //        them into separate databases and you'd need MSDTC, which is
        //        almost never worth it).
        //
        //    (b) opts.Policies.UseDurableLocalQueues() — a policy that
        //        enrolls ALL local queues into durable inbox/outbox
        //        processing. Without this, messages are stored in-memory
        //        only and lost on process restart. With this, every locally
        //        dispatched message is persisted to the message store and
        //        durably delivered.
        //
        //    (c) opts.UseEntityFrameworkCoreTransactions() — registers EF Core
        //        as the transaction provider for Wolverine handlers. Each
        //        handler that uses a DbContext is automatically enrolled in
        //        an EF Core transaction: the middleware calls
        //        BeginTransactionAsync before the handler runs, CommitAsync
        //        after it returns successfully, and RollbackAsync if it
        //        throws.
        //
        //        This ALSO solves the Identity auto-commit issue: when
        //        UserManager.CreateAsync calls Context.SaveChangesAsync
        //        internally, the save happens INSIDE the open transaction —
        //        it's NOT auto-committed. If the handler later fails, the
        //        transaction rolls back, including the Identity rows. No
        //        TransactionScope needed.
        //
        //    (d) opts.Policies.AutoApplyTransactions() — auto-applies the
        //        transactional middleware to EVERY handler that uses a
        //        DbContext. Without this, you'd have to annotate each
        //        handler with [Transactional]. With it, the default is
        //        "transactional" — opt OUT with [NonTransactional] on the
        //        rare handler that needs to.
        //
        //    (e) opts.PublishDomainEventsFromEntityFrameworkCore<AggregateRoot,
        //        BaseDomainEvent>(agg => agg.DomainEvents) — registers a
        //        domain-event scraper that, at commit time, walks the
        //        ChangeTracker, filters to entities assignable to
        //        AggregateRoot (so SaleLineItem and other non-aggregate
        //        entities are silently skipped), reads their DomainEvents
        //        collection, and publishes each via IMessageBus.PublishAsync.
        //        The published messages are enrolled in the SAME EF Core
        //        transaction (because the handler is running under the
        //        transactional middleware), so they commit atomically with
        //        the business changes.
        //
        //        This replaces what would otherwise be a hand-rolled
        //        ISaveChangesInterceptor. The interceptor approach has two
        //        problems that the built-in scraper avoids:
        //          1. Captive dependency: IMessageBus is Scoped, but
        //             DbContextOptions (which holds interceptors) is
        //             Singleton. A Singleton-bound interceptor cannot safely
        //             inject a Scoped IMessageBus.
        //          2. Timing: calling IMessageBus.PublishAsync from inside
        //             SavingChangesAsync (the "before save" hook) adds
        //             OutgoingMessage entities to the DbContext AFTER
        //             DetectChanges has run, so they may not be in the same
        //             SaveChanges round-trip.
        //
        //        The built-in scraper runs in EfCoreEnvelopeTransaction.CommitAsync
        //        — AFTER SaveChanges succeeds, BEFORE the transaction commits.
        //        This is the correct point in the lifecycle.
        //
        //    CAVEAT — ClearDomainEvents:
        //        Wolverine's scraper does NOT clear the events from the
        //        aggregate after publishing. This is intentional — the
        //        aggregate is a Scoped entity (loaded fresh per handler
        //        invocation), so its in-memory DomainEvents collection is
        //        discarded with the scope. If you ever cache aggregates or
        //        reuse DbContext instances across messages, you'd need to
        //        call ClearDomainEvents() yourself after the scrape. For our
        //        current Scoped-per-request pattern, no action needed.
        //
        //    WHAT THIS BUYS US (all five pieces together):
        //      - When a handler calls IMessageBus.PublishAsync(...) inside
        //        a transaction, the published message is written to the
        //        wolverine_messages table in the SAME transaction as the
        //        business changes.
        //      - If SaveChangesAsync succeeds, both the business changes
        //        and the message are committed atomically.
        //      - If SaveChangesAsync fails, both roll back — no orphan
        //        messages, no lost events.
        //      - A background process then picks up the outbox entries
        //        and sends them to their actual destinations.
        //
        //    Reference:
        //      https://wolverinefx.net/guide/durability/sqlserver
        //      https://wolverinefx.net/guide/durability/efcore/outbox-and-inbox
        //      https://wolverinefx.net/guide/durability/efcore/transactional-middleware
        //      https://wolverinefx.net/guide/durability/efcore/domain-events
        // ------------------------------------------------------------------
        services.Configure<WolverineOptions>(opts =>
        {
            // (a) SQL Server message store — durably persists outgoing
            //     messages until processed. Uses the same connection string
            //     as ApplicationDbContext (captured from the local
            //     `databaseOptions` var constructed in section 1 above).
            //     Connection string never appears in any method parameter.
            opts.PersistMessagesWithSqlServer(databaseOptions.ConnectionString);

            // (b) Durable local queues — enrolls ALL local queues into
            //     durable inbox/outbox processing. Without this, messages
            //     are stored in-memory only and lost on process restart.
            opts.Policies.UseDurableLocalQueues();

            // (c) Register EF Core as the transaction provider.
            opts.UseEntityFrameworkCoreTransactions();

            // (d) Auto-apply transactional middleware to all handlers using
            //     a DbContext. Individual handlers can opt out with
            //     [NonTransactional] (namespace Wolverine.Attributes).
            opts.Policies.AutoApplyTransactions();

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
            opts.PublishDomainEventsFromEntityFrameworkCore<AggregateRoot, BaseDomainEvent>(
                agg => agg.DomainEvents);
        });

        return services;
    }
}
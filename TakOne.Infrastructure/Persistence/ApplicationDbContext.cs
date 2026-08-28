using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Common.Entities;
using TakOne.Domain.Customers.Entities;
using TakOne.Domain.Notifications.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Users;
using TakOne.Infrastructure.Identity;
using TakOne.SharedKernel.Common;
using TakOne.SharedKernel.Primitives;

namespace TakOne.Infrastructure.Persistence;

/// <summary>
/// The single EF Core DbContext for the entire TakOne application.
///
/// RESPONSIBILITIES:
///   - Maps all Domain aggregates (Category, Product, Sale, User) to SQL Server tables.
///   - Acts as the IdentityDbContext for ASP.NET Identity (AspNetUsers, AspNetRoles,
///     AspNetUserRoles, AspNetUserClaims, AspNetUserLogins, AspNetUserTokens,
///     AspNetRoleClaims). The Identity user entity is <see cref="ApplicationUser"/>.
///   - Serves as the Unit-of-Work boundary: a single <c>SaveChangesAsync</c> call
///     commits ALL changes (domain aggregates + identity tables + outbox entries)
///     in one transaction.
///   - Stores the ASP.NET Core Data Protection key ring in a <c>DataProtectionKeys</c>
///     table (see <see cref="DataProtectionKeys"/> below). Implemented via
///     <see cref="IDataProtectionKeyContext"/> so that
///     <c>PersistKeysToDbContext&lt;ApplicationDbContext&gt;()</c> in
///     <c>TakOne.WebUI/Program.cs</c> can persist keys to this DbContext.
///
/// WHY ONE DbContext (not separate ones for Domain and Identity):
///   The Application layer's command handlers sometimes need to create a Domain
///   <c>User</c> AND a corresponding <c>ApplicationUser</c> (with password + role)
///   in the same transaction. If they used separate DbContexts, we'd need a
///   distributed transaction (MSDTC) to keep them atomic — which is fragile,
///   slow, and often unavailable on cloud SQL Server. Sharing one DbContext
///   means both rows commit (or roll back) together, naturally.
///
/// DBSET NAMING:
///   - <see cref="DomainUsers"/> (not <c>Users</c>) — because
///     <c>IdentityDbContext</c> already exposes a <c>Users</c> DbSet for
///     <see cref="ApplicationUser"/>. Renaming ours avoids the collision and
///     makes it obvious at the call site which "user" you mean.
///   - All other DbSet names are pluralized entity names (Products, Categories,
///     SubCategories, SubSubCategories, Sales, SaleLineItems, DataProtectionKeys).
///
/// CONFIGURATION DISCOVERY:
///   Entity configurations live in <c>Persistence/Configurations/</c> as separate
///   <c>IEntityTypeConfiguration&lt;T&gt;</c> classes. They're picked up
///   automatically by <see cref="OnModelCreating"/> via
///   <c>ApplyConfigurationsFromAssembly</c> — no manual registration needed
///   when adding a new entity.
///
///   The Data Protection key entity (<c>DataProtectionKey</c> from the
///   <c>Microsoft.AspNetCore.DataProtection.EntityFrameworkCore</c> package)
///   is mapped by EF Core CONVENTION (no IEntityTypeConfiguration needed) —
///   see <see cref="DataProtectionKeys"/> below for details.
/// </summary>
public class ApplicationDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>,
      IDataProtectionKeyContext
{
    /// <summary>
    /// Domain User aggregate (NOT the Identity user). Maps to the
    /// <c>Users</c> table. Shares its primary key with the corresponding
    /// <see cref="ApplicationUser"/> (in <c>AspNetUsers</c>) — the application
    /// layer is responsible for ensuring both rows are created with the same
    /// Guid Id, in the same transaction.
    /// </summary>
    public DbSet<User> DomainUsers { get; set; } = null!;

    /// <summary>
    /// Product aggregate root. Maps to the <c>Products</c> table.
    /// </summary>
    public DbSet<Product> Products { get; set; } = null!;

    /// <summary>
    /// Category aggregate root. Maps to the <c>Categories</c> table.
    /// </summary>
    public DbSet<Category> Categories { get; set; } = null!;

    /// <summary>
    /// SubCategory entity (inside the Category aggregate boundary). Exposed
    /// as a DbSet so repository code can query it directly when needed
    /// (e.g. <c>ICategoryRepository.SubCategoryBelongsToCategoryAsync</c>).
    /// Maps to the <c>SubCategories</c> table.
    /// </summary>
    public DbSet<SubCategory> SubCategories { get; set; } = null!;

    /// <summary>
    /// SubSubCategory entity (inside the Category aggregate boundary). Same
    /// rationale as <see cref="SubCategories"/>. Maps to the
    /// <c>SubSubCategories</c> table.
    /// </summary>
    public DbSet<SubSubCategory> SubSubCategories { get; set; } = null!;

    /// <summary>
    /// Sale aggregate root. Maps to the <c>Sales</c> table.
    /// </summary>
    public DbSet<Sale> Sales { get; set; } = null!;

    /// <summary>
    /// SaleLineItem entity (inside the Sale aggregate boundary). Exposed as a
    /// DbSet for direct querying (e.g. reporting). Maps to the
    /// <c>SaleLineItems</c> table.
    /// </summary>
    public DbSet<SaleLineItem> SaleLineItems { get; set; } = null!;

    /// <summary>
    /// SaleSequenceCounter entity — one row per Persian year, holding the
    /// NEXT sequence number to allocate for that year. This is the
    /// AUTHORITATIVE source of truth for SaleNumber sequence allocation,
    /// replacing the old "Count(sales in year) + 1" / "MAX(Sequence) + 1"
    /// algorithms which had hard-delete reuse and year-boundary invisibility
    /// bugs. See <see cref="SaleSequenceCounter"/> for the full rationale.
    ///
    /// Maps to the <c>SaleSequenceCounters</c> table.
    /// </summary>
    public DbSet<SaleSequenceCounter> SaleSequenceCounters { get; set; } = null!;

    /// <summary>
    /// CustomerGroup aggregate root — a named bucket of customers who
    /// share the same monthly salary budget and the same per-product
    /// purchase-limit table. Maps to the <c>CustomerGroups</c> table.
    ///
    /// Referenced by <c>Users.GroupId</c> (nullable FK) and by
    /// <c>ProductPurchaseLimits.GroupId</c> (FK inside the Product
    /// aggregate's OwnsMany block).
    /// </summary>
    public DbSet<CustomerGroup> CustomerGroups { get; set; } = null!;

    /// <summary>
    /// SystemSettings singleton entity — application-wide configuration
    /// that admins can change at runtime (no app restart required).
    /// Maps to the <c>SystemSettings</c> table. Always contains exactly
    /// one row, identified by <see cref="SystemSettings.SingletonId"/>.
    ///
    /// Reads are cached in-process via <c>ISystemSettingsService</c>
    /// so the hot-path purchase-limit checks don't hit the DB.
    /// </summary>
    public DbSet<SystemSettings> SystemSettings { get; set; } = null!;

    /// <summary>
    /// Notification aggregate root — a single user-targeted notification row
    /// (e.g. "your order INT-1505-00000042 was approved"). Created
    /// atomically inside the same EF Core transaction as the triggering
    /// sale mutation; persisted read state (ReadAtUtc) survives circuit
    /// restarts and multi-device logins.
    /// Maps to the <c>Notifications</c> table.
    /// </summary>
    public DbSet<Notification> Notifications { get; set; } = null!;

    /// <summary>
    /// BroadcastNotification aggregate root — the admin's audit-record view
    /// of an admin-authored broadcast (or the auto-emitted app-update
    /// broadcast). One row per broadcast (sent-by, when, scope, target,
    /// title, message, recipient count). The per-user fanout rows live in
    /// <see cref="Notifications"/> with <c>Kind=Broadcast</c> (or AppUpdate)
    /// and a <c>BroadcastId</c> back-pointer to this aggregate's Id.
    /// Maps to the <c>BroadcastNotifications</c> table.
    /// </summary>
    public DbSet<BroadcastNotification> BroadcastNotifications { get; set; } = null!;

    /// <summary>
    /// ASP.NET Core Data Protection key ring. One row per key. The
    /// <c>DataProtectionKey</c> entity (from
    /// <c>Microsoft.AspNetCore.DataProtection.EntityFrameworkCore</c>)
    /// carries: <c>Id</c> (int, SQL Server identity), <c>FriendlyName</c>
    /// (a human-readable label, e.g. the key id as a Guid string), and
    /// <c>Xml</c> (the full key material XML — same format as the
    /// file-system store would have written to
    /// <c>.dataprotection-keys/key-*.xml</c>).
    ///
    /// This DbSet is REQUIRED by <see cref="IDataProtectionKeyContext"/>
    /// and is what <c>PersistKeysToDbContext&lt;ApplicationDbContext&gt;()</c>
    /// reads/writes when the Data Protection subsystem creates, rotates, or
    /// revokes keys. The auth cookie (TakOne.Auth) and antiforgery tokens
    /// are encrypted with keys from this ring.
    ///
    /// MAPPING:
    ///   Mapped by EF Core CONVENTION (no IEntityTypeConfiguration needed).
    ///   Table name defaults to <c>DataProtectionKeys</c>. See the migration
    ///   <c>20260805092026_AddDataProtectionKeys</c> for the concrete schema.
    ///
    /// SECURITY NOTE: the XML in this column is in CLEAR TEXT inside the
    /// database — same as it would be on disk under
    /// <c>PersistKeysToFileSystem</c>. The security boundary is SQL Server
    /// access control: only the application's DB user has SELECT/INSERT on
    /// this table. If you need defense-in-depth beyond SQL access control
    /// (e.g. to protect against DBA-level access), layer
    /// <c>ProtectKeysWithCertificate(...)</c> with an X.509 cert from the
    /// machine cert store — no cloud service required.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;


    /// <summary>
    /// Standard constructor. Called by <c>AddDbContext&lt;ApplicationDbContext&gt;</c>
    /// in <c>AddTakOneInfrastructure</c> (Step 7e).
    /// </summary>
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }


    /// <summary>
    /// Configures the model. Order of operations:
    ///   1. Call <c>base.OnModelCreating</c> — registers all ASP.NET Identity
    ///      tables (AspNetUsers, AspNetRoles, etc.) with their default schema.
    ///   2. <c>ApplyConfigurationsFromAssembly</c> — picks up every
    ///      <c>IEntityTypeConfiguration&lt;T&gt;</c> class in this assembly
    ///      (the files in <c>Persistence/Configurations/</c>).
    ///
    /// We deliberately do NOT register any inline configurations here —
    /// everything lives in dedicated configuration classes for testability
    /// and separation of concerns.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // IMPORTANT: must be called first. IdentityDbContext registers its
        // own entities (ApplicationUser, IdentityRole, IdentityUserClaim, etc.)
        // and any override of those (e.g. renaming Identity tables). Our
        // configurations are applied AFTER, so we can override Identity
        // defaults if we ever need to.
        // CA1725: parameter renamed from `modelBuilder` to `builder` to match
        // the base IdentityDbContext.OnModelCreating(ModelBuilder builder)
        // signature — parameter-name drift between override and base is what
        // the analyzer catches.
        base.OnModelCreating(builder);

        // ------------------------------------------------------------------
        // Domain Events are NOT persisted — they're in-memory only.
        //
        // AggregateRoot exposes `public IReadOnlyCollection<BaseDomainEvent>
        // DomainEvents`. EF Core's navigation discovery sees this typed
        // collection and tries to map BaseDomainEvent (and every derived
        // event type) as an entity. BaseDomainEvent has no Id property,
        // so EF throws "requires a primary key to be defined" at
        // design-time (e.g. `dotnet ef migrations add`).
        //
        // We don't want domain events in the DB at all. They're scraped
        // from the aggregate by Wolverine's
        // `PublishDomainEventsFromEntityFrameworkCore<AggregateRoot,
        // BaseDomainEvent>(agg => agg.DomainEvents)` extension in
        // ServiceCollectionExtensions, then dispatched by Wolverine as
        // messages through the enrolled outbox (atomic with the
        // originating SaveChangesAsync transaction).
        //
        // `Ignore<BaseDomainEvent>()` removes it from the model
        // discovery graph entirely — and because derived types are only
        // discovered through their base, this also covers
        // SaleCreatedDomainEvent, SaleApprovedDomainEvent,
        // CategoryCreatedDomainEvent, etc. No per-event Ignore needed.
        // ------------------------------------------------------------------
        builder.Ignore<BaseDomainEvent>();

        // Apply all IEntityTypeConfiguration<T> classes in this assembly.
        // This is the modern alternative to manually calling
        // `builder.Entity<Category>().HasKey(...)` etc. — each entity's
        // mapping lives in its own file, which is easier to read, test, and
        // maintain.
        //
        // NOTE on DataProtectionKey: it is NOT configured by an
        // IEntityTypeConfiguration<T> in this assembly, and
        // ApplyConfigurationsFromAssembly only scans the CURRENT assembly
        // (the Infrastructure assembly), not the NuGet package's assembly.
        // Instead, DataProtectionKey is mapped by EF Core CONVENTION:
        //   - PK is `Id` (int, SQL Server identity-by-default).
        //   - Table name is `DataProtectionKeys` (pluralized entity name).
        //   - `FriendlyName` and `Xml` are nvarchar(max) columns.
        // No explicit configuration is needed — exposing the
        // DbSet<DataProtectionKey> (declared above) is sufficient for the
        // conventions to map it. See migration
        // `20260805092026_AddDataProtectionKeys` for the concrete schema.
        builder.ApplyConfigurationsFromAssembly(
            typeof(ApplicationDbContext).Assembly);

        // ------------------------------------------------------------------
        // Optimistic-concurrency convention (Brutal Code Review v3 #14).
        //
        // AggregateRoot now exposes a `byte[] RowVersion` property. We
        // configure EVERY entity type that has a RowVersion property as a
        // SQL Server `rowversion` column via `.IsRowVersion()`. This is
        // the EF Core convention for optimistic concurrency: the DB
        // auto-increments the rowversion on every UPDATE; when two
        // concurrent transactions load the same row and both try to save,
        // the second save's WHERE clause (RowVersion = @original) fails
        // to match, and EF throws DbUpdateConcurrencyException — which
        // handlers catch and surface as a friendly retry error.
        //
        // This convention-based approach means: adding RowVersion to
        // AggregateRoot is the ONLY change needed — no per-entity
        // IEntityTypeConfiguration boilerplate. Every aggregate that
        // inherits AggregateRoot (Sale, Product, Category, User,
        // CustomerGroup, Notification, etc.) gets the token automatically.
        // ------------------------------------------------------------------
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            // Find a RowVersion property on this entity type (declared
            // or inherited). Skip if none — owned/complex types and
            // ASP.NET Identity entities (ApplicationUser is NOT an
            // AggregateRoot) don't have it.
            var rowVersionProperty = entityType.FindProperty(nameof(AggregateRoot.RowVersion));
            if (rowVersionProperty is null)
            {
                continue;
            }

            // Configure as a SQL Server rowversion column. This is what
            // the `.IsRowVersion()` fluent-API extension does internally,
            // expressed via the low-level IMutableProperty API so it
            // works in a convention loop without a PropertyBuilder:
            //   1. IsConcurrencyToken = true  → EF includes the column
            //      in the WHERE clause of UPDATE/DELETE statements
            //      (the concurrency check).
            //   2. SetColumnType("rowversion") → SQL Server maps the
            //      column as a rowversion (auto-incrementing binary).
            //   3. ValueGenerated = OnAddOrUpdate → the DB assigns the
            //      value on INSERT and updates it on every UPDATE; EF
            //      reads back the new value so the in-memory entity
            //      stays consistent with the DB.
            //   4. SetDefaultValue(Array.Empty<byte>()) → CRITICAL for
            //      the SQLite in-memory integration tests. SQLite has
            //      no native rowversion type (the "rowversion" column
            //      type is treated as an opaque BLOB), so SQLite does
            //      NOT auto-generate a value on INSERT. Without a
            //      default, every seed INSERT (e.g. SystemSettings
            //      singleton seed) fails with "NOT NULL constraint
            //      failed: <Table>.RowVersion". The default (empty
            //      byte array) lets the INSERT succeed; EF Core's
            //      concurrency check still fires on UPDATE (it
            //      compares the original RowVersion to the DB's current
            //      value in the WHERE clause). On SQL Server (production),
            //      the default is only used by the ALTER TABLE ADD
            //      COLUMN migration step to populate existing rows —
            //      new INSERTs auto-generate a real rowversion value
            //      and the default is never read. See Brutal Code Review
            //      v3 finding #14 + the Round 18-F test discovery.
            rowVersionProperty.IsConcurrencyToken = true;
            rowVersionProperty.SetColumnType("rowversion");
            rowVersionProperty.ValueGenerated = ValueGenerated.OnAddOrUpdate;
            rowVersionProperty.SetDefaultValue(Array.Empty<byte>());
        }
    }
}
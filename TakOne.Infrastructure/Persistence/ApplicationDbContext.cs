using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TakOne.Domain.Categories.Entities;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Sales.Entities;
using TakOne.Domain.Users;
using TakOne.Infrastructure.Identity;

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
///     SubCategories, SubSubCategories, Sales, SaleLineItems).
///
/// CONFIGURATION DISCOVERY:
///   Entity configurations live in <c>Persistence/Configurations/</c> as separate
///   <c>IEntityTypeConfiguration&lt;T&gt;</c> classes. They're picked up
///   automatically by <see cref="OnModelCreating"/> via
///   <c>ApplyConfigurationsFromAssembly</c> — no manual registration needed
///   when adding a new entity.
/// </summary>
public class ApplicationDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
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
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // IMPORTANT: must be called first. IdentityDbContext registers its
        // own entities (ApplicationUser, IdentityRole, IdentityUserClaim, etc.)
        // and any override of those (e.g. renaming Identity tables). Our
        // configurations are applied AFTER, so we can override Identity
        // defaults if we ever need to.
        base.OnModelCreating(modelBuilder);

        // Apply all IEntityTypeConfiguration<T> classes in this assembly.
        // This is the modern alternative to manually calling
        // `modelBuilder.Entity<Category>().HasKey(...)` etc. — each entity's
        // mapping lives in its own file, which is easier to read, test, and
        // maintain.
        modelBuilder.ApplyConfigurationsFromAssembly
            (typeof(ApplicationDbContext).Assembly);
    }
}
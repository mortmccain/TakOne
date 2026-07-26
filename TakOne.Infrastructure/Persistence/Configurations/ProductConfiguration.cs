using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Products.Entities;
using TakOne.Domain.Products.ValueObjects;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="Product"/> aggregate root.
///
/// TABLE: <c>Products</c>
///
/// COLUMNS:
///   - Id                (uniqueidentifier, PK)
///   - Name              (nvarchar(200), NOT NULL)
///   - Description       (nvarchar(2000), NOT NULL)
///   - PictureUrl        (nvarchar(max), NULL)
///   - Price_Amount      (decimal(18, 2), NOT NULL) — owned Money value object
///   - Price_Currency    (nvarchar(3), NOT NULL)    — owned Money value object
///   - StockQuantity     (int, NOT NULL)
///   - CategoryId        (uniqueidentifier, NOT NULL, INDEXED — cross-aggregate ref, NO FK)
///   - SubCategoryId     (uniqueidentifier, NULL, INDEXED — cross-aggregate ref, NO FK)
///   - SubSubCategoryId  (uniqueidentifier, NULL, INDEXED — cross-aggregate ref, NO FK)
///
/// OWNED COLLECTION: <c>ProductPurchaseLimits</c> (one row per CustomerGroupPurchaseLimit)
///   - Id            (int, IDENTITY, shadow PK — EF requires a PK; the domain value object has no Id)
///   - ProductId     (uniqueidentifier, FK → Products.Id, cascade delete)
///   - GroupName     (nvarchar(100), NOT NULL)
///   - Limit         (int, NOT NULL)
///   - Unique index on (ProductId, GroupName) — one limit per group per product
///
/// CROSS-AGGREGATE REFERENCES — WHY NO FKs:
///   In strict DDD, aggregates should be independent persistence units.
///   Enforcing a DB-level FK from Product.CategoryId → Categories.Id would
///   couple the Product and Category aggregates at the database level:
///     - Deleting a Category would require cascading or blocking behavior
///       that the domain doesn't want (we use soft-delete for Categories,
///       not hard delete, so this is mostly hypothetical — but the principle
///       holds).
///     - Bulk-importing Products without Categories existing yet would fail.
///     - Sharding aggregates across databases in the future would break.
///   Instead, we use INDEXES on the cross-aggregate Guid columns for query
///   performance, and the application layer enforces referential integrity
///   (e.g. CreateProductCommandHandler loads the Category to verify it exists
///   before creating the Product).
///
/// MONEY VALUE OBJECT MAPPING:
///   <c>OwnsOne</c> flattens the Money value object into the parent table.
///   Columns are named <c>{NavigationName}_{PropertyName}</c> by EF convention,
///   so <c>Product.Price</c> becomes <c>Price_Amount</c> and
///   <c>Price_Currency</c>. Money has no identity of its own — it's a value
///   object — so OwnsOne is the correct mapping (not a separate entity table).
/// </summary>
public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        // ------------------------------------------------------------------
        // Table & primary key
        // ------------------------------------------------------------------
        builder.ToTable("Products");
        builder.HasKey(p => p.Id);

        // ------------------------------------------------------------------
        // Scalar columns
        // ------------------------------------------------------------------
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(2000).IsRequired();

        // PictureUrl is optional — a product may be created without a picture
        // and have one added later. nvarchar(max) because URLs can be long
        // (especially if they include query strings / SAS tokens).
        builder.Property(p => p.PictureUrl).HasMaxLength(int.MaxValue);

        builder.Property(p => p.StockQuantity).IsRequired();

        // Cross-aggregate references — indexed for query performance, but NO
        // foreign key constraints (see class-level docs for rationale).
        builder.Property(p => p.CategoryId).IsRequired();
        builder.Property(p => p.SubCategoryId);
        builder.Property(p => p.SubSubCategoryId);

        // ------------------------------------------------------------------
        // Owned value object: Price (Money)
        //
        // OwnsOne flattens Money's two properties (Amount, Currency) into the
        // Products table as `Price_Amount` and `Price_Currency`. The Money
        // type itself has no Id and no identity of its own — it's a value
        // object — so it CANNOT be a separate entity. OwnsOne is the only
        // correct mapping.
        // ------------------------------------------------------------------
        builder.OwnsOne(p => p.Price, price =>
        {
            // decimal(18, 2) — 18 digits total, 2 after the decimal point.
            // This is the standard SQL Server money-like precision and works
            // for IRR (no decimal subunits) and most fiat currencies. For
            // crypto or 4-decimal-place currencies, bump to (18, 4).
            price.Property(m => m.Amount)
                .HasColumnName("Price_Amount")
                .HasPrecision(18, 2)
                .IsRequired();

            // Currency is a 3-letter ISO 4217 code (e.g. "USD", "IRR").
            // Fixed-length 3 would be ideal, but EF Core + SQL Server prefers
            // nvarchar(3) for compatibility with comparisons.
            price.Property(m => m.Currency)
                .HasColumnName("Price_Currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        // ------------------------------------------------------------------
        // Owned collection: PurchaseLimits (List<CustomerGroupPurchaseLimit>)
        //
        // OwnsMany because CustomerGroupPurchaseLimit is a VALUE OBJECT
        // (no Id, identity is its property values). EF requires a PK on every
        // table, so we let it create a shadow `Id` int identity column.
        //
        // The domain never reads or writes this shadow Id — it's purely an
        // EF/SQL requirement. From the domain's perspective, the value
        // object's identity is (GroupName, Limit).
        //
        // WHY NO HasOne/HasMany here:
        //   OwnsMany ALREADY declares the relationship: it creates a shadow
        //   `ProductId` FK column, sets cascade delete, and marks the entity
        //   as owned. Calling `HasOne<Product>().WithMany().HasForeignKey(...)`
        //   ON TOP of OwnsMany re-declares the relationship as a non-ownership
        //   navigation, which EF rejects with:
        //     "The navigation 'PurchaseLimits' cannot be changed, because the
        //      foreign key between 'Product' and 'CustomerGroupPurchaseLimit'
        //      is an ownership. To change the navigation to the owned entity
        //      type remove the ownership."
        //   We rely purely on OwnsMany's implicit ownership + cascade.
        // ------------------------------------------------------------------
        builder.OwnsMany(p => p.PurchaseLimits, limit =>
        {
            limit.ToTable("ProductPurchaseLimits");

            // Shadow primary key — EF needs SOMETHING to be the PK. Use an
            // int identity so the rows have a stable ordering for diagnostics.
            // The domain never sees this value.
            limit.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnName("Id");

            limit.HasKey("Id");

            // NOTE: the ProductId shadow FK column + cascade-delete behavior
            // are AUTOMATICALLY set up by OwnsMany. Do NOT add a HasOne here
            // — see the block comment above for the rationale.

            limit.Property(l => l.GroupName).HasMaxLength(100).IsRequired();
            limit.Property(l => l.Limit).IsRequired();

            // Unique index: a product can have AT MOST ONE limit per group.
            // The domain's SetPurchaseLimit method enforces this in-memory
            // (it removes the existing limit before adding the new one), but
            // the DB index is the authoritative guard against races.
            //
            // "ProductId" here is a string referring to the shadow FK property
            // that OwnsMany created. EF resolves it by name.
            limit.HasIndex("ProductId", nameof(CustomerGroupPurchaseLimit.GroupName))
                .IsUnique();
        });

        // Use the private backing field for the PurchaseLimits collection.
        // (Same pattern as CategoryConfiguration — the public property is
        // IReadOnlyList<T> with no setter.)
        builder.Metadata.FindNavigation(nameof(Product.PurchaseLimits))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // ------------------------------------------------------------------
        // Indexes (cross-aggregate references + common query patterns)
        // ------------------------------------------------------------------
        // Indexes on CategoryId / SubCategoryId / SubSubCategoryId so the
        // shop's "browse by category" queries are fast. These are NON-UNIQUE
        // (many products can share a category).
        builder.HasIndex(p => p.CategoryId);
        builder.HasIndex(p => p.SubCategoryId);
        builder.HasIndex(p => p.SubSubCategoryId);

        // Non-unique index on Name for the admin product-search feature
        // (LIKE 'term%' queries can use this index for prefix matches).
        builder.HasIndex(p => p.Name);
    }
}
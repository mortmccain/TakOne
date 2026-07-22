using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Categories.Entities;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="Category"/> aggregate root.
///
/// TABLE: <c>Categories</c>
///
/// COLUMNS:
///   - Id            (uniqueidentifier, PK)
///   - Name          (nvarchar(100), NOT NULL)
///   - IsActive      (bit, NOT NULL)
///
/// RELATIONSHIPS:
///   - One Category → Many SubCategories (configured in
///     <see cref="SubCategoryConfiguration"/>, FK = SubCategory.CategoryId,
///     cascade delete).
///   - EF discovers the <c>_subCategories</c> backing field by convention
///     (camelCase + underscore prefix matching the public
///     <c>SubCategories</c> property name). We set
///     <c>PropertyAccessMode.Field</c> explicitly so EF ALWAYS uses the field
///     (the public property is <c>IReadOnlyList&lt;T&gt;</c> with no setter,
///     so the property setter wouldn't work anyway).
///
/// INDEXES:
///   - Unique index on <c>Name</c> (case-insensitive via collation) so that
///     two active categories can't share the same name. The application
///     layer (<c>NameExistsAsync</c>) checks this before insert, but the
///     DB index is the last line of defense against race conditions.
/// </summary>
public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        // ------------------------------------------------------------------
        // Table & primary key
        // ------------------------------------------------------------------
        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);

        // ------------------------------------------------------------------
        // Columns
        // ------------------------------------------------------------------
        builder.Property(c => c.Name)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(c => c.IsActive)
            .IsRequired();

        // ------------------------------------------------------------------
        // SubCategories collection — use the private backing field.
        //
        // The public property is `IReadOnlyList<SubCategory>` (no setter), so
        // EF must populate the private `_subCategories` field directly. The
        // `HasField` + `UsePropertyAccessMode(Field)` combination tells EF:
        //   1. The field exists.
        //   2. Always read/write through the field, never through the property.
        //
        // The one-to-many relationship is configured here for clarity, but
        // the FK itself is declared on the SubCategory side (see
        // SubCategoryConfiguration).
        // ------------------------------------------------------------------
        builder.HasMany(c => c.SubCategories)
            .WithOne()
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(Category.SubCategories))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // ------------------------------------------------------------------
        // Indexes
        // ------------------------------------------------------------------
        // Unique name within the Categories table. Two categories with the
        // same name would be confusing for users and would make name-based
        // lookups ambiguous. The application layer checks this too, but the
        // DB index is the authoritative guard.
        builder.HasIndex(c => c.Name).IsUnique();
    }
}
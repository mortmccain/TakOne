using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Categories.Entities;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="SubCategory"/> entity (lives inside the
/// Category aggregate boundary).
///
/// TABLE: <c>SubCategories</c>
///
/// COLUMNS:
///   - Id            (uniqueidentifier, PK)
///   - CategoryId    (uniqueidentifier, FK → Categories.Id, NOT NULL)
///   - Name          (nvarchar(100), NOT NULL)
///   - IsActive      (bit, NOT NULL)
///
/// RELATIONSHIPS:
///   - Many SubCategories → One Category (FK = SubCategory.CategoryId,
///     cascade delete from the Category side).
///   - One SubCategory → Many SubSubCategories (configured in
///     <see cref="SubSubCategoryConfiguration"/>, FK =
///     SubSubCategory.SubCategoryId, cascade delete).
///
/// INDEXES:
///   - Unique composite index on <c>(CategoryId, Name)</c> so that
///     SubCategory names are unique WITHIN a Category (two different
///     Categories can each have a SubCategory named "Electronics", but one
///     Category can't have two). The domain enforces this via
///     <c>Category.EnsureSubCategoryNameUnique</c>; the DB index is the
///     authoritative guard against races.
/// </summary>
public sealed class SubCategoryConfiguration : IEntityTypeConfiguration<SubCategory>
{
    public void Configure(EntityTypeBuilder<SubCategory> builder)
    {
        builder.ToTable("SubCategories");
        builder.HasKey(s => s.Id);

        // Id is client-generated (set in the constructor via base(Guid.NewGuid())).
        // ValueGeneratedNever() is REQUIRED to avoid the EF Core NavigationFixer
        // bug where new entities added to a navigation collection of a tracked
        // (Unchanged/Modified) principal are incorrectly marked as Modified
        // instead of Added. See SaleLineItemConfiguration for the full rationale.
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.CategoryId).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(100).IsRequired();
        builder.Property(s => s.IsActive).IsRequired();

        // SubSubCategories collection — same field-access pattern as
        // CategoryConfiguration. The public property is IReadOnlyList<T>
        // with no setter, so EF must use the private _subSubCategories field.
        builder.HasMany(s => s.SubSubCategories)
            .WithOne()
            .HasForeignKey(ss => ss.SubCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Metadata.FindNavigation(nameof(SubCategory.SubSubCategories))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        // Composite unique index: SubCategory name is unique within its parent
        // Category. (Two different Categories can each have a SubCategory
        // named "X" — that's allowed. But one Category can't have two.)
        builder.HasIndex(s => new { s.CategoryId, s.Name }).IsUnique();
    }
}
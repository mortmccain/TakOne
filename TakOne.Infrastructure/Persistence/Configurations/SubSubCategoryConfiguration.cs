using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Categories.Entities;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="SubSubCategory"/> entity (lives inside
/// the Category aggregate boundary, one level below SubCategory).
///
/// TABLE: <c>SubSubCategories</c>
///
/// COLUMNS:
///   - Id            (uniqueidentifier, PK)
///   - SubCategoryId (uniqueidentifier, FK → SubCategories.Id, NOT NULL)
///   - Name          (nvarchar(100), NOT NULL)
///   - IsActive      (bit, NOT NULL)
///
/// INDEXES:
///   - Unique composite index on <c>(SubCategoryId, Name)</c> — same
///     reasoning as <see cref="SubCategoryConfiguration"/>: SubSubCategory
///     names are unique within their parent SubCategory.
/// </summary>
public sealed class SubSubCategoryConfiguration : IEntityTypeConfiguration<SubSubCategory>
{
    public void Configure(EntityTypeBuilder<SubSubCategory> builder)
    {
        builder.ToTable("SubSubCategories");
        builder.HasKey(ss => ss.Id);

        builder.Property(ss => ss.SubCategoryId).IsRequired();
        builder.Property(ss => ss.Name).HasMaxLength(100).IsRequired();
        builder.Property(ss => ss.IsActive).IsRequired();

        // Composite unique index: SubSubCategory name is unique within its
        // parent SubCategory. Domain enforcement is in SubCategory.
        // EnsureSubSubCategoryNameUnique; the DB index is the authoritative
        // race-condition guard.
        builder.HasIndex(ss => new { ss.SubCategoryId, ss.Name }).IsUnique();
    }
}
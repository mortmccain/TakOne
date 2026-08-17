using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Customers.Entities;
using TakOne.SharedKernel.ValueObjects;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the <see cref="CustomerGroup"/> aggregate root.
///
/// TABLE: <c>CustomerGroups</c>
///
/// COLUMNS:
///   - Id              (uniqueidentifier, PK)
///   - Name            (nvarchar(100), NOT NULL, UNIQUE)
///   - Salary_Amount   (decimal(18, 2), NOT NULL) — owned Money value object
///   - Salary_Currency (nvarchar(3), NOT NULL)    — owned Money value object
///   - IsActive        (bit, NOT NULL)
///   - CreatedAt       (datetime2, NOT NULL)
///   - UpdatedAt       (datetime2, NOT NULL)
///
/// RELATIONSHIPS OWNED BY OTHERS:
///   - <c>Users.GroupId</c> references this table's Id (configured in
///     <see cref="UserConfiguration"/> as a real FK with cascade-restrict
///     behavior — see that file's class-level docs).
///   - <c>ProductPurchaseLimits.GroupId</c> references this table's Id
///     (configured in <see cref="ProductConfiguration"/>'s OwnsMany block).
///
/// MONEY VALUE OBJECT MAPPING:
///   <see cref="CustomerGroup.Salary"/> is mapped as a ComplexProperty
///   (EF Core 9+ value-object mapping) for the same reasons as
///   <c>Product.Price</c> — value semantics, no concurrency-tracking pain
///   on reference replacement. Flattens to <c>Salary_Amount</c> and
///   <c>Salary_Currency</c> columns on the CustomerGroups table.
/// </summary>
public sealed class CustomerGroupConfiguration : IEntityTypeConfiguration<CustomerGroup>
{
    public void Configure(EntityTypeBuilder<CustomerGroup> builder)
    {
        // ------------------------------------------------------------------
        // Table & primary key
        // ------------------------------------------------------------------
        builder.ToTable("CustomerGroups");
        builder.HasKey(g => g.Id);

        // ------------------------------------------------------------------
        // Scalar columns
        // ------------------------------------------------------------------
        builder.Property(g => g.Name).HasMaxLength(100).IsRequired();

        builder.Property(g => g.IsActive).IsRequired();

        // Timestamps — datetime2(7) is EF Core's default for DateTime and
        // has sub-millisecond precision, which is what we want for audit.
        builder.Property(g => g.CreatedAt).IsRequired();
        builder.Property(g => g.UpdatedAt).IsRequired();

        // ------------------------------------------------------------------
        // Complex value object: Salary (Money)
        //
        // Same pattern as Product.Price — ComplexProperty with column names
        // <c>Salary_Amount</c> (decimal(18,2)) and <c>Salary_Currency</c>
        // (nvarchar(3)). See ProductConfiguration for the full rationale
        // of ComplexProperty vs OwnsOne.
        // ------------------------------------------------------------------
        builder.ComplexProperty(g => g.Salary, salary =>
        {
            salary.Property(m => m.Amount)
                .HasColumnName("Salary_Amount")
                .HasPrecision(18, 2)
                .IsRequired();

            salary.Property(m => m.Currency)
                .HasColumnName("Salary_Currency")
                .HasMaxLength(3)
                .IsRequired();
        });

        // ------------------------------------------------------------------
        // Indexes
        // ------------------------------------------------------------------
        // Unique index on Name — group names must be unique. Enforces the
        // domain invariant "two groups cannot have the same name" at the
        // database level (the domain layer's guard only checks length).
        //
        // A duplicate-name INSERT will throw DbUpdateException with a
        // unique-constraint violation — the application layer translates
        // that to a friendly localized error (Step 9).
        builder.HasIndex(g => g.Name).IsUnique();

        // Non-unique index on IsActive — speeds up the "active groups only"
        // query on the Manage Groups page. Most groups are active so the
        // index is selective only when there are inactive groups.
        builder.HasIndex(g => g.IsActive);
    }
}
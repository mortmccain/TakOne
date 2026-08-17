using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TakOne.Domain.Users;

namespace TakOne.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for the Domain <see cref="User"/> aggregate.
///
/// TABLE: <c>Users</c>
///
/// COLUMNS:
///   - Id         (uniqueidentifier, PK)
///   - WorkerId   (nvarchar(100), NOT NULL, UNIQUE)
///   - FullName   (nvarchar(200), NOT NULL)
///   - GroupId    (uniqueidentifier, NULL — FK to CustomerGroups.Id)
///   - Gender     (int, NOT NULL, default 0 = Male)
///   - IsActive   (bit, NOT NULL)
///
/// RELATIONSHIP TO ASP.NET IDENTITY's <c>ApplicationUser</c>:
///   The Domain User and ApplicationUser are TWO SEPARATE entities that
///   share the SAME primary key (a Guid). They live in TWO SEPARATE tables:
///     - <c>Users</c>          (this configuration — Domain User)
///     - <c>AspNetUsers</c>    (ApplicationUser, configured by IdentityDbContext)
///
///   We deliberately do NOT define a DB-level foreign key between them.
///   Reasons:
///     1. They're created in the same transaction by the application layer,
///        but ordering matters (Domain User first, then ApplicationUser with
///        the same Id). A circular FK would make this impossible.
///     2. The Domain doesn't know about Identity — adding an FK from
///        <c>Users.Id</c> to <c>AspNetUsers.Id</c> would be a layering
///        violation (Infrastructure-only concern leaking into Domain mapping).
///   Instead, the application layer enforces the invariant "if a Domain User
///   exists, an ApplicationUser with the same Id also exists" via the
///   <c>IUserAccountService.CreateIdentityAccountAsync</c> method.
///
/// RELATIONSHIP TO <c>CustomerGroup</c> (Salary-feature Step 2):
///   <c>GroupId</c> is a proper FK to <c>CustomerGroups.Id</c>. Null is
///   allowed (staff users have no group). The FK uses <c>Restrict</c>
///   delete behavior — deleting a CustomerGroup that still has users is
///   rejected by the DB. The application layer must reassign or remove all
///   users from a group before deleting it (see Step 5's delete-group flow).
///
/// INDEXES:
///   - Unique index on <c>WorkerId</c> — login identifier, must be unique.
///   - Non-unique index on <c>GroupId</c> — for the staff dashboard query
///     "all customers in group X" (used by IUserRepository.GetByGroupIdAsync).
///   - Non-unique index on <c>IsActive</c> — for the admin "active/inactive
///     users" filter (used by IUserRepository.GetPaginatedAsync).
///
/// GENDER (Phase 0.5):
///   Stored as <c>int</c> (EF Core's default enum-to-int mapping — no
///   .HasConversion() needed). The column is NOT NULL with a default value
///   of 0 (Male) so that the Phase 0.5 migration can backfill existing
///   rows without breaking the NOT NULL constraint. New rows always have
///   a value because the Domain factory sets it explicitly (defaulting to
///   Male if the caller doesn't specify).
/// </summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.WorkerId).HasMaxLength(100).IsRequired();
        builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();

        // ------------------------------------------------------------------
        // GroupId — nullable FK to CustomerGroups.Id.
        //
        // Staff users (employees, managers, read-only, admin) have
        // GroupId = null. Only customers are assigned to a group.
        //
        // The column is nullable, but the FK constraint enforces that any
        // non-null value MUST reference an existing CustomerGroup.Id.
        // ------------------------------------------------------------------
        builder.Property(u => u.GroupId);

        // FK relationship: User.GroupId → CustomerGroups.Id, Restrict delete.
        // Restrict means: trying to delete a CustomerGroup that still has
        // users referencing it will throw a DbUpdateException (FK violation)
        // — the application layer must reassign or remove users first.
        builder.HasOne<TakOne.Domain.Customers.Entities.CustomerGroup>()
            .WithMany()
            .HasForeignKey(u => u.GroupId)
            .OnDelete(DeleteBehavior.Restrict);

        // Gender — stored as int (EF Core's default enum mapping). NOT NULL
        // with column default 0 (Male) so existing rows survive the
        // AddGenderToUsers migration. See class-level remark for rationale.
        builder.Property(u => u.Gender)
            .HasConversion<int>()
            .IsRequired()
            .HasDefaultValue(Gender.Male);

        builder.Property(u => u.IsActive).IsRequired();

        // WorkerId is the login identifier — it MUST be unique. The
        // application layer checks this in WorkerIdExistsAsync before
        // creating a user, but the DB index is the authoritative guard
        // against races.
        builder.HasIndex(u => u.WorkerId).IsUnique();

        // GroupId index — used by GetByGroupIdAsync and the GroupId filter
        // in GetPaginatedAsync. Non-unique (many users share a group).
        builder.HasIndex(u => u.GroupId);

        // IsActive index — used by the active/inactive filter in
        // GetPaginatedAsync. Non-unique (most users are active).
        builder.HasIndex(u => u.IsActive);
    }
}
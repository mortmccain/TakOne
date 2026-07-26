namespace TakOne.Domain.Users;

/// <summary>
/// Gender of a TakOne user.
///
/// LOCKED-IN DECISION (roadmap Section 12.5):
///   2-value enum only — <see cref="Male"/> + <see cref="Female"/>.
///   No <c>Other</c> / <c>PreferNotToSay</c> / <c>PreferNotToAnswer</c>.
///   The user explicitly chose this minimal set.
///
/// DEFAULT:
///   <see cref="Male"/> (value 0). When the Gender column is added to
///   existing rows via migration, the column default is 0 — so all
///   pre-existing users (if any) become Male. New users default to Male
///   unless the creator explicitly chooses Female.
///
/// STORAGE:
///   Stored as an <c>int</c> column in the <c>Users</c> table (see
///   <c>UserConfiguration</c>). EF Core's default enum-to-int mapping
///   is sufficient — no .HasConversion() needed.
///
/// WHERE IT LIVES:
///   In the Domain layer because it's a domain fact about a User, not an
///   Identity concern. Mirrors the pattern used for FullName / GroupName —
///   the Domain User owns the fact, and at login time it's also copied
///   onto the ApplicationUser (denormalized) so admin user-management
///   pages can display it without a join.
/// </summary>
public enum Gender
{
    /// <summary>
    /// Male. Value 0 (also the column default for existing rows).
    /// </summary>
    Male = 0,

    /// <summary>
    /// Female. Value 1.
    /// </summary>
    Female = 1
}
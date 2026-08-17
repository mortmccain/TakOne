using TakOne.Domain.Users;

namespace TakOne.Application.Users.DTOs;

/// <summary>
/// Read-side DTO for a single User. Combines domain fields (from the
/// <c>User</c> aggregate) with ASP.NET Identity fields (email, roles)
/// fetched via <c>IUserAccountService</c>.
///
/// GROUP FIELD SEMANTICS (Salary feature, Step 3):
///   <list type="bullet">
///     <item><c>GroupId</c> — the FK to <c>CustomerGroups.Id</c>. Null for
///         staff users (who have no group).</item>
///     <item><c>GroupName</c> — the DISPLAY name of the group, looked up
///         via <c>ICustomerGroupRepository.GetByIdAsync</c>. Populated
///         ONLY when the caller is Admin/Manager — null otherwise.</item>
///   </list>
///
///   Customers never see the word "group" in any UI string. The
///   <c>GroupName</c> field here is internal-staff-only display data.
///
/// Gender is included so the admin user-management page (Phase 7.3) and
/// the user's own profile page (Phase 6.1) can display it without a
/// separate query. Per roadmap Section 12.5, it's a 2-value enum
/// (Male / Female).
/// </summary>
public sealed class UserDto
{
    public Guid Id { get; init; }
    public string WorkerId { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;

    /// <summary>
    /// The user's gender (Phase 0.5). Always populated — defaults to Male
    /// at the Domain factory if not explicitly set at creation time.
    /// </summary>
    public Gender Gender { get; init; }

    /// <summary>
    /// The customer group Id (FK to CustomerGroups.Id). Null for staff
    /// users (who have no group). Always populated for customers.
    /// </summary>
    public Guid? GroupId { get; init; }

    /// <summary>
    /// The customer group's DISPLAY NAME — populated via a single
    /// <c>ICustomerGroupRepository.GetByIdAsync</c> call. Null when:
    ///   - The user is staff (no group)
    ///   - The caller is Employee (Employees don't see group names —
    ///     they only need to know THAT a user is in a group, not which one)
    ///   - The group was hard-deleted (defensive — shouldn't happen
    ///     since CustomerGroup uses soft-delete via IsActive=false)
    ///
    /// Exposed ONLY to admins and managers — never to customers themselves.
    /// The query handler that builds this DTO is responsible for respecting
    /// the caller's role.
    /// </summary>
    public string? GroupName { get; init; }

    public bool IsActive { get; init; }

    /// <summary>
    /// Email address. Lives on ApplicationUser (ASP.NET Identity), not on
    /// the domain User.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// ASP.NET Identity roles assigned to the user (e.g. ["Customer"],
    /// ["Employee"], ["Manager", "Customer"]). A user may have multiple
    /// roles — managers and employees are typically also Customers so they
    /// can buy on their own behalf.
    /// </summary>
    public List<string> Roles { get; init; } = new();
}
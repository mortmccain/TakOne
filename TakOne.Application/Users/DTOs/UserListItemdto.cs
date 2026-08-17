using TakOne.Domain.Users;

namespace TakOne.Application.Users.DTOs;

/// <summary>
/// Lightweight DTO for paginated user lists (admin user-management page,
/// staff dashboard). Excludes email and roles — fetch <see cref="UserDto"/>
/// via GetById if you need them.
///
/// GROUP FIELD SEMANTICS (Salary feature, Step 3):
///   <list type="bullet">
///     <item><c>GroupId</c> — the FK to <c>CustomerGroups.Id</c>. Always
///         populated when the user is in a group; null for staff.</item>
///     <item><c>GroupName</c> — the DISPLAY name of the group, looked up
///         via <c>ICustomerGroupRepository</c> in a single batched call.
///         Populated ONLY when the caller is Admin/Manager — null
///         otherwise (Employees, Customers never see group names of
///         other users).</item>
///   </list>
///
///   Customers never see the word "group" in any UI string. The
///   <c>GroupName</c> field here is internal-staff-only display data.
/// </summary>
public sealed class UserListItemDto
{
    public Guid Id { get; init; }
    public string WorkerId { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;

    /// <summary>
    /// The user's gender (Phase 0.5). Displayed on the admin user-management
    /// list so admins can scan for data-entry errors at a glance.
    /// </summary>
    public Gender Gender { get; init; }

    /// <summary>
    /// The customer group Id (FK to CustomerGroups.Id). Null for staff
    /// users (who have no group). Always populated for customers —
    /// use this for any programmatic lookup (e.g. filtering limits).
    /// </summary>
    public Guid? GroupId { get; init; }

    /// <summary>
    /// The customer group's DISPLAY NAME — populated via a batched
    /// <c>ICustomerGroupRepository.GetByIdsAsync</c> call in the handler.
    /// Null when:
    ///   - The user is staff (no group)
    ///   - The caller is Employee (Employees don't see group names —
    ///     they only need to know THAT a user is in a group, not which one)
    ///   - The group was hard-deleted (defensive — shouldn't happen
    ///     since CustomerGroup uses soft-delete via IsActive=false)
    /// </summary>
    public string? GroupName { get; init; }

    public bool IsActive { get; init; }

    /// <summary>
    /// The ASP.NET Identity role names this user belongs to (e.g.
    /// "Employee", "Manager", "Customer"). Loaded in batch by the
    /// GetUsersPaginated query handler via IUserRepository.GetRolesByUserIdsAsync.
    ///
    /// Used by the AdminUsers page to gate the activate/deactivate button:
    /// a Manager (non-Admin) may only activate/deactivate users whose
    /// Roles collection contains <c>Roles.Employee</c> — they cannot
    /// act on other managers, admins, read-onlys, or customers.
    /// </summary>
    public List<string> Roles { get; init; } = new();
}
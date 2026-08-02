using TakOne.Domain.Users;

namespace TakOne.Application.Users.DTOs;

/// <summary>
/// Lightweight DTO for paginated user lists (admin user-management page,
/// staff dashboard). Excludes email and roles — fetch <see cref="UserDto"/>
/// via GetById if you need them.
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
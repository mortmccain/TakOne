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
    public string? GroupName { get; init; }
    public bool IsActive { get; init; }
}
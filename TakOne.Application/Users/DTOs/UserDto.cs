namespace TakOne.Application.Users.DTOs;

/// <summary>
/// Read-side DTO for a single User. Combines domain fields (from the
/// <c>User</c> aggregate) with ASP.NET Identity fields (email, roles)
/// fetched via <c>IUserAccountService</c>.
///
/// GroupName is exposed here for ADMIN views only — customer-facing views
/// must NEVER include it. The query handler that builds this DTO is
/// responsible for respecting the caller's role.
/// </summary>
public sealed class UserDto
{
    public Guid Id { get; init; }
    public string WorkerId { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;

    /// <summary>
    /// The customer group, or null for staff users. Expose ONLY to admins
    /// and managers — never to customers themselves.
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
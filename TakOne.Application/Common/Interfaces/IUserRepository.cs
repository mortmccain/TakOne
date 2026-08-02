using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Read/write repository for the <see cref="User"/> aggregate.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<User?> GetByWorkerIdAsync(string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all users in a given customer group. Used by staff dashboards
    /// to list customers per group.
    /// </summary>
    Task<List<User>> GetByGroupNameAsync(string groupName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of users, optionally filtered by a free-text
    /// search term (matched against WorkerId and FullName, case-insensitive),
    /// an IsActive filter, and/or a GroupName filter.
    ///
    /// Used by the admin user-management page (all users) and by staff
    /// dashboards (e.g. "all customers in group X").
    ///
    /// Pass <c>isActive: null</c> to include both active and inactive users.
    /// Pass <c>groupName: null</c> to include users from all groups (and
    /// staff users who have no group).
    /// </summary>
    Task<PaginatedResult<User>> GetPaginatedAsync(
        string? searchTerm = null,
        bool? isActive = null,
        string? groupName = null,
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> WorkerIdExistsAsync(string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct set of customer group names that currently have
    /// at least one user assigned. Used by the CreateProduct page to render
    /// a dropdown of "known groups" so staff can attach per-group purchase
    /// limits without having to type the group name from memory.
    ///
    /// Staff users have <c>GroupName = null</c> and are excluded. Returns
    /// an empty list (not null) when no customer groups exist yet.
    /// </summary>
    Task<List<string>> GetDistinctGroupNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of active users currently in the <c>Customer</c>
    /// role. Used by the Dashboard's "Active Customers" KPI card.
    ///
    /// IMPLEMENTATION NOTE:
    ///   The Domain User table (<c>Users</c>) doesn't store roles — roles
    ///   live in ASP.NET Identity's <c>AspNetUserRoles</c> + <c>AspNetRoles</c>
    ///   tables, joined to <c>AspNetUsers</c> by user Id. The Infrastructure
    ///   implementation therefore joins
    ///   <c>AspNetUsers (IsActive=1) → AspNetUserRoles → AspNetRoles (Name='Customer')</c>.
    ///
    ///   The join crosses the Domain/Identity boundary, which is why this
    ///   method lives on the repository (Infrastructure) rather than being
    ///   computed in the application layer — the Application layer has no
    ///   direct access to the Identity tables.
    /// </summary>
    Task<int> GetActiveCustomerCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a dictionary mapping user Id → list of ASP.NET Identity role
    /// names for the given user Ids. Used by GetUsersPaginatedQueryHandler to
    /// populate <c>UserListItemDto.Roles</c> in a single batched query (rather
    /// than N+1 queries per user).
    ///
    /// Users with no roles (rare — only happens if role seeding is incomplete)
    /// simply don't appear as a key in the returned dictionary. Callers
    /// should treat a missing key as "no roles".
    ///
    /// Same Domain/Identity boundary note as <see cref="GetActiveCustomerCountAsync"/>:
    /// roles live in AspNetUserRoles + AspNetRoles, not on the Domain User.
    /// </summary>
    Task<Dictionary<Guid, List<string>>> GetRolesByUserIdsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default);

    Task AddAsync(User user, CancellationToken cancellationToken = default);
}
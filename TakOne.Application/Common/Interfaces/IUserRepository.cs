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
    Task<PaginatedResult<User>> GetPaginatedAsync
        (
        string? searchTerm = null,
        bool? isActive = null,
        string? groupName = null,
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default
        );

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> WorkerIdExistsAsync(string workerId, CancellationToken cancellationToken = default);

    Task AddAsync(User user, CancellationToken cancellationToken = default);
}
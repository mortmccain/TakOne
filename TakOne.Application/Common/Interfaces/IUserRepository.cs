using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Common.Interfaces;

/// <summary>
/// Read/write repository for the <see cref="User"/> aggregate.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Batch read-only load of users by Id, AsNoTracking. Used by
    /// <c>GetBroadcastNotificationsQueryHandler</c> to resolve sender +
    /// target-user names for a page of audit rows in a single round-trip
    /// (instead of one <c>GetByIdAsync</c> call per row — an N+1 that
    /// costs N round-trips per page render). Empty input returns an empty
    /// list without hitting the DB; missing Ids are simply absent from
    /// the result.
    /// </summary>
    Task<List<User>> GetByIdsReadOnlyAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default);

    Task<User?> GetByWorkerIdAsync(string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all users in a given customer group. Used by staff dashboards
    /// to list customers per group.
    /// </summary>
    Task<List<User>> GetByGroupIdAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of users, optionally filtered by a free-text
    /// search term (matched against WorkerId and FullName, case-insensitive),
    /// an IsActive filter, and/or a GroupId filter.
    ///
    /// Used by the admin user-management page (all users) and by staff
    /// dashboards (e.g. "all customers in group X").
    ///
    /// Pass <c>isActive: null</c> to include both active and inactive users.
    /// Pass <c>groupId: null</c> to include users from all groups (and
    /// staff users who have no group).
    /// </summary>
    Task<PaginatedResult<User>> GetPaginatedAsync(
        string? searchTerm = null,
        bool? isActive = null,
        Guid? groupId = null,
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default);

    Task<bool> WorkerIdExistsAsync(string workerId, CancellationToken cancellationToken = default);

    // NOTE: GetDistinctGroupNamesAsync was REMOVED in Step 2 of the salary
    // feature. Group names are no longer stored on User — they live on the
    // CustomerGroup aggregate. To list all groups, use
    // ICustomerGroupRepository.GetAllAsync.

    /// <summary>
    /// Returns the Id of every ACTIVE user in the system. Used by the
    /// broadcast notification fanout when <c>Scope=All</c> (admin-authored
    /// global announcement, or the auto-emitted app-update broadcast).
    /// </summary>
    /// <remarks>
    /// <b>WHY Ids ONLY (not full User entities)</b>: the fanout only needs
    /// the recipient Guids to create per-user Notification rows. Loading
    /// full entities would N times the memory + the EF change-tracker
    /// population for no benefit. A projection to <c>Guid</c> is one round-trip
    /// and zero tracking overhead.
    /// </remarks>
    Task<List<Guid>> GetAllActiveUserIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the Ids of every ACTIVE user currently in the given ASP.NET
    /// Identity role. Used by the broadcast fanout when <c>Scope=Role</c>.
    /// </summary>
    /// <param name="roleName">
    /// A role name from <see cref="Authorization.Roles"/> (e.g.
    /// <c>Roles.Customer</c>, <c>Roles.Employee</c>).
    /// </param>
    /// <remarks>
    /// <b>DOMAIN/IDENTITY BOUNDARY</b>: same note as
    /// <see cref="GetActiveCustomerCountAsync"/> — roles live in
    /// <c>AspNetUserRoles</c> + <c>AspNetRoles</c>, joined to <c>Users</c> by
    /// user Id. The Infrastructure implementation does this join server-side.
    /// </remarks>
    Task<List<Guid>> GetActiveUserIdsInRoleAsync(string roleName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the Ids of every ACTIVE user assigned to the given customer
    /// group. Used by the broadcast fanout when <c>Scope=Group</c>.
    /// </summary>
    /// <remarks>
    /// <b>WHY A NEW METHOD (vs. GetByGroupIdAsync)</b>: the existing
    /// <see cref="GetByGroupIdAsync"/> returns full tracked <c>User</c>
    /// entities (the caller — the staff dashboard — wants to render names +
    /// badges). The broadcast fanout only needs Guids, and loading full
    /// entities would bloat the change tracker for an admin broadcast that
    /// could fan out to hundreds of users. A dedicated Ids-only projection
    /// is one round-trip + zero tracking overhead.
    /// </remarks>
    Task<List<Guid>> GetActiveUserIdsInGroupAsync(Guid groupId, CancellationToken cancellationToken = default);

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
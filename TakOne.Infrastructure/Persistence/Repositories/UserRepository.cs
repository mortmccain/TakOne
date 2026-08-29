using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Users.Queries.GetUsersPaginated;
using TakOne.Domain.Users;
using TakOne.SharedKernel.Common;

namespace TakOne.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserRepository"/>.
///
/// DBSET NOTE:
///   This repository uses <see cref="ApplicationDbContext.DomainUsers"/>, NOT
///   the <c>Users</c> DbSet inherited from <c>IdentityDbContext</c> (which is
///   typed as <c>DbSet&lt;ApplicationUser&gt;</c>). Domain User and Identity
///   User live in separate tables (<c>Users</c> and <c>AspNetUsers</c>) with
///   a shared PK — see <see cref="UserConfiguration"/> class-level docs.
///
/// TRACKING POLICY:
///   Same as <see cref="ProductRepository"/>: command handlers load a User,
///   call domain methods on it (e.g. <c>ChangeFullName</c>, <c>Deactivate</c>,
///   <c>AssignToGroup</c>), then call <c>IUnitOfWork.SaveChangesAsync</c>.
///   So reads are TRACKED (no AsNoTracking).
///
/// WORKERID LOGIN-IDENTIFIER LOOKUP:
///   <see cref="GetByWorkerIdAsync"/> uses <c>SingleOrDefaultAsync</c>
///   (not <c>FirstOrDefaultAsync</c>) — the unique index on WorkerId (see
///   UserConfiguration) guarantees at most one match, so SingleOrDefault's
///   "throw if more than one" behavior is a defensive invariant check. If
///   the index ever drops, we want to know loudly, not silently take the
///   first row.
///
/// PAGINATED SEARCH SEMANTICS:
///   <see cref="GetPaginatedAsync"/>'s searchTerm matches WorkerId OR FullName
///   (case-insensitive Contains). The OR is important: an admin searching
///   "EMP-123" expects to find that workerId, but an admin searching "smith"
///   expects to find by name. We can't know which the user typed, so we OR.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly ApplicationDbContext _db;

    public UserRepository(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // FindAsync: see ProductRepository.GetByIdAsync for rationale.
        return await _db.DomainUsers.FindAsync(new object[] { id }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<User>> GetByIdsReadOnlyAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        // AsNoTracking — pure read path for the broadcast-audit name resolver.
        // ToListAsync materializes the batch in a single round-trip. Missing
        // Ids are simply absent from the result (callers use GetValueOrDefault
        // on the dictionary built from this list). ICollection materialization
        // avoids double-enumeration of an IEnumerable when the EF Core
        // provider translates `ids.Contains(...)` into a SQL IN clause.
        var idList = ids as ICollection<Guid> ?? ids.ToList();
        if (idList.Count == 0)
        {
            return new List<User>(0);
        }

        return await _db.DomainUsers
            .AsNoTracking()
            .Where(u => idList.Contains(u.Id))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<User?> GetByWorkerIdAsync(string workerId, CancellationToken cancellationToken = default)
    {
        // SingleOrDefaultAsync: WorkerId has a unique index, so at most one
        // match. SingleOrDefault throws if more than one is found — defensive
        // against a dropped/missing unique index. (FirstOrDefaultAsync would
        // silently return the first row, masking the bug.)
        //
        // WorkerId is the user's login identifier — this method is called by
        // CreateSaleCommandHandler when starting a sale on behalf of a customer
        // (the customer is identified by WorkerId at the UI layer).
        return await _db.DomainUsers
            .SingleOrDefaultAsync(u => u.WorkerId == workerId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<User>> GetByGroupIdAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        // Returns all users in a customer group. The GroupId index makes
        // this query fast. We don't paginate here because customer groups are
        // expected to be small (typically &lt;100 users per group); if a group
        // ever exceeds ~500 users, the staff dashboard query should switch to
        // GetPaginatedAsync with groupId filter.
        //
        // Order by FullName for stable UI rendering.
        return await _db.DomainUsers
            .Where(u => u.GroupId == groupId)
            .OrderBy(u => u.FullName)
            .ToListAsync(cancellationToken);
    }

    // ── Round 5: server-side text filters + sort ──────────────────────
    //
    // The string predicates below (both in GetPaginatedAsync's search-term
    // lambda and in the ApplyTextFilter expression trees) use the
    // parameterless string.ToLower()/Contains(string) deliberately: those
    // are the overloads EF Core translates to LOWER()/LIKE. The
    // culture-taking overloads the analyzers prefer are NOT translatable
    // (same rationale as SalesSpecificationFilters).
#pragma warning disable CA1304 // ToLower culture — SQL LOWER() has no culture
#pragma warning disable CA1311 // Contains culture — same
#pragma warning disable CA1862 // OrdinalIgnoreCase overload — not EF-translatable

    /// <inheritdoc />
    public async Task<PaginatedResult<User>> GetPaginatedAsync(
        UsersListFilters? filters,
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Defensive: clamp page parameters. See ProductRepository for rationale.
        // ------------------------------------------------------------------
        pageNumber = pageNumber < 1 ? 1 : pageNumber;
        pageSize = pageSize < 1 ? 20 : pageSize;

        filters ??= new UsersListFilters(
            SearchTerm: null,
            GroupId: null,
            IsActive: null,
            Gender: null,
            WorkerId: null,
            FullName: null,
            SortBy: null,
            SortDescending: false);

        // ------------------------------------------------------------------
        // Build the filter query. Each conditional Where is a no-op when the
        // filter value is null. Every predicate below must translate to SQL
        // on both SQL Server (production) and SQLite (integration tests) —
        // same EF-translatability contract as the sales list (see
        // SalesSpecificationFilters): plain parameterless ToLower()/
        // Contains(string) so string matching is case-insensitive on BOTH
        // providers (SQLite's default collation is case-SENSITIVE, so the
        // pre-Round-5 bare Contains was not actually case-insensitive there).
        // ------------------------------------------------------------------
        var query = _db.DomainUsers.AsQueryable();

        if (filters.IsActive is not null)
        {
            query = query.Where(u => u.IsActive == filters.IsActive.Value);
        }

        if (filters.GroupId is not null)
        {
            query = query.Where(u => u.GroupId == filters.GroupId.Value);
        }

        if (filters.Gender is not null)
        {
            // Gender is stored as its int ordinal; comparing the enum
            // directly translates to an int comparison in SQL.
            query = query.Where(u => u.Gender == filters.Gender.Value);
        }

        // searchTerm matches WorkerId OR FullName. We OR them so a search for
        // "EMP-123" finds the workerId AND a search for "smith" finds the name.
        //
        // We trim and skip empty so an empty search box returns all users
        // (rather than filtering out users with null fields).
        var trimmedSearch = filters.SearchTerm?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedSearch))
        {
            var searchLower = trimmedSearch.ToLowerInvariant();
            query = query.Where(u =>
                u.WorkerId.ToLower().Contains(searchLower) ||
                u.FullName.ToLower().Contains(searchLower));
        }

        // Typed per-column text filters (Round 5). Hand-built expression
        // trees — same technique as SalesSpecificationFilters.ApplyTextFilter
        // — so the final tree stays a plain MemberAccess → method-call chain
        // that EF translates to LOWER()/LIKE. Unknown operators are skipped
        // (lenient no-filter).
        query = ApplyTextFilter(query, u => u.WorkerId, filters.WorkerId);
        query = ApplyTextFilter(query, u => u.FullName, filters.FullName);

        // ------------------------------------------------------------------
        // Total count — must be on the filtered query, before pagination.
        // ------------------------------------------------------------------
        var totalCount = await query.CountAsync(cancellationToken);

        // ------------------------------------------------------------------
        // Apply ordering + pagination. Default = FullName ascending (the
        // pre-Round-5 order the mobile list + typeahead rely on); every arm
        // carries the Id tiebreaker so OFFSET/FETCH paging can never skip
        // or duplicate rows (equal WorkerIds / FullNames / Genders are
        // stable across page boundaries).
        // ------------------------------------------------------------------
        query = ApplySort(query, filters.SortBy, filters.SortDescending);

        var items = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<User>(items, totalCount, pageNumber, pageSize);
    }

    private static IQueryable<User> ApplyTextFilter(
        IQueryable<User> query,
        Expression<Func<User, string>> selector,
        UsersTextFilter? filter)
    {
        var term = filter?.Value?.Trim();
        if (filter is null || string.IsNullOrEmpty(term))
        {
            return query;
        }

        var value = term.ToLowerInvariant();

        // The selector is a simple member expression (WorkerId / FullName),
        // so rebuilding the body per operator is a matter of wrapping the
        // lowered member in the operator's string method call. Built by
        // hand (not via a captured sub-lambda) so the final tree stays a
        // plain MemberAccess → method-call chain that EF translates to
        // LOWER()/LIKE.
        var body = selector.Body;
        var user = selector.Parameters[0];
        var lowered = Expression.Call(body, ToLowerMethod);

        Expression? predicate = filter.Operator switch
        {
            UsersTextOperator.Contains =>
                Expression.Call(lowered, ContainsMethod, Expression.Constant(value)),

            UsersTextOperator.NotContains =>
                Expression.Not(Expression.Call(
                    lowered, ContainsMethod, Expression.Constant(value))),

            UsersTextOperator.Equals =>
                Expression.Equal(lowered, Expression.Constant(value)),

            UsersTextOperator.NotEquals =>
                Expression.NotEqual(lowered, Expression.Constant(value)),

            UsersTextOperator.StartsWith =>
                Expression.Call(lowered, StartsWithMethod, Expression.Constant(value)),

            UsersTextOperator.EndsWith =>
                Expression.Call(lowered, EndsWithMethod, Expression.Constant(value)),

            // Unknown operator values (a malformed message could carry an
            // out-of-range enum) are ignored — lenient no-filter.
            _ => null
        };

        if (predicate is null)
        {
            return query;
        }

        return query.Where(Expression.Lambda<Func<User, bool>>(predicate, user));
    }

    private static IQueryable<User> ApplySort(
        IQueryable<User> query, UsersSortBy? sortBy, bool descending)
    {
        return (sortBy ?? UsersSortBy.FullName, descending) switch
        {
            (UsersSortBy.WorkerId, false) => query.OrderBy(u => u.WorkerId).ThenBy(u => u.Id),
            (UsersSortBy.WorkerId, true) => query.OrderByDescending(u => u.WorkerId).ThenByDescending(u => u.Id),
            (UsersSortBy.FullName, false) => query.OrderBy(u => u.FullName).ThenBy(u => u.Id),
            (UsersSortBy.FullName, true) => query.OrderByDescending(u => u.FullName).ThenByDescending(u => u.Id),
            // Gender/IsActive are enum/bool columns — SQL orders by their
            // int representation; the Id tiebreaker keeps equal groups
            // deterministic.
            (UsersSortBy.Gender, false) => query.OrderBy(u => u.Gender).ThenBy(u => u.Id),
            (UsersSortBy.Gender, true) => query.OrderByDescending(u => u.Gender).ThenByDescending(u => u.Id),
            (UsersSortBy.IsActive, false) => query.OrderBy(u => u.IsActive).ThenBy(u => u.Id),
            (UsersSortBy.IsActive, true) => query.OrderByDescending(u => u.IsActive).ThenByDescending(u => u.Id),
            _ => query.OrderBy(u => u.FullName).ThenBy(u => u.Id)
        };
    }

#pragma warning restore CA1304
#pragma warning restore CA1311
#pragma warning restore CA1862

    private static readonly MethodInfo ToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) })!;

    private static readonly MethodInfo StartsWithMethod =
        typeof(string).GetMethod(nameof(string.StartsWith), new[] { typeof(string) })!;

    private static readonly MethodInfo EndsWithMethod =
        typeof(string).GetMethod(nameof(string.EndsWith), new[] { typeof(string) })!;

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _db.DomainUsers.AnyAsync(u => u.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> WorkerIdExistsAsync(string workerId, CancellationToken cancellationToken = default)
    {
        // Used by CreateCustomer / CreateStaff handlers to enforce WorkerId
        // uniqueness BEFORE attempting to create the Domain User (which would
        // otherwise fail at SaveChanges with a SQL unique-constraint
        // violation — but a friendly pre-check gives a better error message
        // and lets the handler skip the related ApplicationUser creation).
        return await _db.DomainUsers.AnyAsync(u => u.WorkerId == workerId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetActiveCustomerCountAsync(CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Cross-Domain/Identity join to count active users in the Customer
        // role. The Domain Users table (DomainUsers DbSet) doesn't store
        // roles — roles live in ASP.NET Identity's AspNetUserRoles +
        // AspNetRoles tables, joined to AspNetUsers by user Id.
        //
        // We need to join:
        //   AspNetUsers  (IsActive = true)        ← _db.Users (ApplicationUser)
        //   AspNetUserRoles (userId, roleId)      ← _db.UserRoles
        //   AspNetRoles  (Name = "Customer")      ← _db.Roles
        //
        // WHY WE USE _db.Users (ApplicationUser) AND NOT _db.DomainUsers:
        //   The IsActive flag is denormalized onto ApplicationUser (see
        //   ApplicationUser.cs in Infrastructure/Identity/). The Domain User
        //   also has IsActive, but the UserRoles join table references
        //   ApplicationUser.Id, so starting from ApplicationUser avoids a
        //   cross-table join through the shared PK.
        //
        // WHY WE LOOK UP THE Customer ROLE ID FIRST (rather than joining by
        // role Name directly):
        //   AspNetUserRoles stores only the RoleId (Guid), not the RoleName.
        //   A single sub-query to resolve the Customer role's Id, then a
        //   second query filtered by that Id, is more cache-friendly than
        //   a 3-table join. EF Core's SQL translation for either approach
        //   is similar, but the two-step form is easier to read.
        // ------------------------------------------------------------------
        var customerRoleId = await _db.Roles
            .Where(r => r.Name == TakOne.Application.Common.Authorization.Roles.Customer)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        // If the Customer role doesn't exist (e.g. RoleSeeder hasn't run
        // yet), return 0 rather than throwing. The dashboard handler also
        // has a try/catch around this call as defense-in-depth.
        if (customerRoleId == Guid.Empty)
        {
            return 0;
        }

        return await _db.Users
            .Where(u => u.IsActive &&
                        _db.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == customerRoleId))
            .CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <summary>
    /// Batched role lookup: returns a dictionary mapping each user Id (from
    /// the input sequence) to its list of ASP.NET Identity role names. Users
    /// with no roles are simply absent from the dictionary — callers should
    /// treat a missing key as "no roles".
    ///
    /// SQL TRANSLATION:
    ///   SELECT ur.UserId, r.Name
    ///   FROM AspNetUserRoles ur
    ///   INNER JOIN AspNetRoles r ON ur.RoleId = r.Id
    ///   WHERE ur.UserId IN ({userIds})
    ///
    /// The grouping into Dictionary&lt;Guid, List&lt;string&gt;&gt; happens
    /// client-side (EF Core translates the join to SQL, then LINQ-to-Objects
    /// does the GroupBy). For the expected list-page size (≤500 users per
    /// page), this is a single round-trip with a small result set.
    /// </summary>
    public async Task<Dictionary<Guid, List<string>>> GetRolesByUserIdsAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken = default)
    {
        // Materialize once so we don't enumerate the source twice (and so
        // EF Core can parameterize the IN clause).
        var idList = userIds?.ToList() ?? new List<Guid>();
        if (idList.Count == 0)
        {
            return new Dictionary<Guid, List<string>>();
        }

        // Join UserRoles → Roles to get (UserId, RoleName) pairs for the
        // requested user Ids. AspNetUserRoles references the ApplicationUser
        // Id, which is the same Guid as the Domain User Id (shared PK —
        // see UserConfiguration class-level docs).
        var pairs = await (
            from ur in _db.UserRoles
            join r in _db.Roles on ur.RoleId equals r.Id
            where idList.Contains(ur.UserId)
            select new { ur.UserId, RoleName = r.Name }
        ).ToListAsync(cancellationToken);

        // Group client-side by UserId. A user with multiple roles produces
        // multiple rows in `pairs`; GroupBy collects them into a List<string>.
        // A user with zero roles produces zero rows and is simply absent
        // from the dictionary (caller treats missing key as "no roles").
        return pairs
            .GroupBy(p => p.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => p.RoleName ?? string.Empty).ToList());
    }

    // NOTE: GetDistinctGroupNamesAsync was REMOVED in Step 2 of the salary
    // feature. Group names are no longer stored on User — they live on the
    // CustomerGroup aggregate. To list all groups, use
    // ICustomerGroupRepository.GetAllAsync.

    /// <inheritdoc />
    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        // AddAsync queues the Domain User for INSERT on the next SaveChanges.
        //
        // IMPORTANT: this method does NOT create the corresponding
        // ApplicationUser (Identity user). That's a separate concern, owned
        // by IUserAccountService.CreateIdentityAccountAsync (Step 7d). The
        // application layer is responsible for calling both in the right
        // order:
        //   1. userRepo.AddAsync(domainUser)              — track for INSERT
        //   2. userAccountService.CreateIdentityAccountAsync(domainUser.Id, ...) — track ApplicationUser
        //   3. unitOfWork.SaveChangesAsync()               — commits both rows
        //                                                  in one transaction
        await _db.DomainUsers.AddAsync(user, cancellationToken);
    }

    // ── BROADCAST FANOUT RECIPIENT RESOLUTION ──────────────────────────
    //
    // Three Ids-only projections used by BroadcastFanout.ExecuteAsync to
    // resolve recipient user Ids for each scope (All / Role / Group). The
    // Scope=User case is handled inline in BroadcastFanout (no repo method
    // needed — it's a single GetByIdAsync call).
    //
    // WHY PROJECTIONS (not full entities): the fanout only needs the Guids
    // to create per-user Notification rows. Loading full User entities
    // would bloat the EF change tracker for a broadcast that could fan
    // out to hundreds of users. A .Select(u => u.Id) projection is one
    // round-trip + zero tracking overhead.

    /// <inheritdoc />
    public async Task<List<Guid>> GetAllActiveUserIdsAsync(CancellationToken cancellationToken = default)
    {
        // Active users only. Uses _db.DomainUsers (the Domain User table)
        // since IsActive is also on Domain User — no need to go through
        // ApplicationUser (the IdentityDbContext's Users DbSet). Same
        // shared-PK convention either way.
        //
        // AsNoTracking — pure read path, no caller mutates these entities.
        // The fanout uses the returned Guids to create NEW Notification
        // rows, never to mutate the User entities themselves.
        return await _db.DomainUsers
            .AsNoTracking()
            .Where(u => u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetActiveUserIdsInRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default)
    {
        // Cross-Domain/Identity join: same pattern as
        // GetActiveCustomerCountAsync but returns the user Ids (not a
        // count) and parameterizes the role name (not hardcoded
        // "Customer").
        //
        // We use _db.Users (ApplicationUser) because the UserRoles join
        // table references ApplicationUser.Id. Starting from ApplicationUser
        // avoids a cross-table join through the shared PK.
        //
        // AsNoTracking — pure read path.
        if (string.IsNullOrEmpty(roleName))
        {
            return new List<Guid>(0);
        }

        return await (
            from u in _db.Users.AsNoTracking()
            join ur in _db.UserRoles on u.Id equals ur.UserId
            join r in _db.Roles on ur.RoleId equals r.Id
            where u.IsActive && r.Name == roleName
            select u.Id
        ).ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<Guid>> GetActiveUserIdsInGroupAsync(
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        // GroupId is on Domain User (not ApplicationUser — see
        // ApplicationUser.cs class-level remark: "GroupName not on
        // ApplicationUser"). So we query _db.DomainUsers directly.
        //
        // AsNoTracking — pure read path.
        return await _db.DomainUsers
            .AsNoTracking()
            .Where(u => u.IsActive && u.GroupId == groupId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }
}
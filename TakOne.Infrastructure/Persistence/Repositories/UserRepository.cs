using Microsoft.EntityFrameworkCore;
using TakOne.Application.Common.Interfaces;
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
    public async Task<List<User>> GetByGroupNameAsync(string groupName, CancellationToken cancellationToken = default)
    {
        // Returns all users in a customer group. The GroupName index makes
        // this query fast. We don't paginate here because customer groups are
        // expected to be small (typically &lt;100 users per group); if a group
        // ever exceeds ~500 users, the staff dashboard query should switch to
        // GetPaginatedAsync with groupName filter.
        //
        // Order by FullName for stable UI rendering.
        return await _db.DomainUsers
            .Where(u => u.GroupName == groupName)
            .OrderBy(u => u.FullName)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PaginatedResult<User>> GetPaginatedAsync(
        string? searchTerm = null,
        bool? isActive = null,
        string? groupName = null,
        int pageNumber = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // ------------------------------------------------------------------
        // Defensive: clamp page parameters. See ProductRepository for rationale.
        // ------------------------------------------------------------------
        pageNumber = pageNumber < 1 ? 1 : pageNumber;
        pageSize = pageSize < 1 ? 20 : pageSize;

        // ------------------------------------------------------------------
        // Build the filter query. Each conditional Where is a no-op when the
        // filter value is null.
        // ------------------------------------------------------------------
        var query = _db.DomainUsers.AsQueryable();

        if (isActive is not null)
        {
            query = query.Where(u => u.IsActive == isActive.Value);
        }

        if (groupName is not null)
        {
            query = query.Where(u => u.GroupName == groupName);
        }

        // searchTerm matches WorkerId OR FullName. We OR them so a search for
        // "EMP-123" finds the workerId AND a search for "smith" finds the name.
        //
        // We trim and skip empty so an empty search box returns all users
        // (rather than filtering out users with null fields).
        var trimmedSearch = searchTerm?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedSearch))
        {
            query = query.Where(u =>
                u.WorkerId.Contains(trimmedSearch) ||
                u.FullName.Contains(trimmedSearch));
        }

        // ------------------------------------------------------------------
        // Total count — must be on the spec'd query, before pagination.
        // ------------------------------------------------------------------
        var totalCount = await query.CountAsync(cancellationToken);

        // ------------------------------------------------------------------
        // Apply ordering + pagination. Order by FullName (not WorkerId) so
        // the user-management UI shows humans in alphabetical order, not
        // grouped by workerId prefix.
        // ------------------------------------------------------------------
        var items = await query
            .OrderBy(u => u.FullName)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<User>(items, totalCount, pageNumber, pageSize);
    }

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
    public async Task<List<string>> GetDistinctGroupNamesAsync(CancellationToken cancellationToken = default)
    {
        // SELECT DISTINCT GroupName FROM DomainUsers
        //   WHERE GroupName IS NOT NULL AND GroupName != ''
        //   ORDER BY GroupName
        //
        // Staff users have GroupName = null and are filtered out. We also
        // filter empty strings defensively (a buggy creation flow could
        // store "" instead of null — the domain guard should prevent it,
        // but this is cheap insurance).
        //
        // Distinct + OrderBy are translated by EF Core into SQL DISTINCT +
        // ORDER BY — single round-trip, no client-side grouping.
        return await _db.DomainUsers
            .Where(u => u.GroupName != null && u.GroupName != string.Empty)
            .Select(u => u.GroupName!)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(cancellationToken);
    }

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
}
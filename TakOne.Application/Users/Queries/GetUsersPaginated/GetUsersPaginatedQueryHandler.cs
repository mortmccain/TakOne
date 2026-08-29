using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Users.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Queries.GetUsersPaginated;

/// <summary>
/// Handler for <see cref="GetUsersPaginatedQuery"/>.
///
/// The handler enforces the per-row GroupName visibility rule by projecting
/// the GroupName field to null for non-admin, non-manager callers. This
/// means the database returns the GroupName, but the wire format omits it.
/// (For a tighter implementation, we could push the visibility rule into
/// the SQL projection itself, but for the expected scale the in-memory
/// strip is fine and keeps the repository contract simple.)
/// </summary>
public sealed class GetUsersPaginatedQueryHandler
{
    private const int MaxPageSize = 100;

    public static async Task<PaginatedResult<UserListItemDto>> HandleAsync
        (
        GetUsersPaginatedQuery query,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        ICustomerGroupRepository customerGroupRepository,
        ILogger<GetUsersPaginatedQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        //
        // Only Admin/Manager/Employee may list users. Customers and
        // read-only users should not even reach this handler — the auth
        // middleware should have rejected the call — but we check anyway
        // in case the middleware is bypassed.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetUsersPaginated: unauthenticated call rejected.");
            return new PaginatedResult<UserListItemDto>(Array.Empty<UserListItemDto>(), 0, 1, 1);
        }

        var canListUsers =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager) ||
            currentUser.IsInRole(Roles.Employee);

        if (!canListUsers)
        {
            // Log the caller's ACTUAL roles (auditors need to know who
            // attempted the access) — the historical version logged the
            // constant role-name list, which is the same for every denial
            // and useless as an audit trail.
            var callerRoles = new List<string>();
            if (currentUser.IsInRole(Roles.Admin)) callerRoles.Add(Roles.Admin);
            if (currentUser.IsInRole(Roles.Manager)) callerRoles.Add(Roles.Manager);
            if (currentUser.IsInRole(Roles.Employee)) callerRoles.Add(Roles.Employee);
            if (currentUser.IsInRole(Roles.Customer)) callerRoles.Add(Roles.Customer);
            if (currentUser.IsInRole(Roles.ReadOnly)) callerRoles.Add(Roles.ReadOnly);
            if (callerRoles.Count == 0) callerRoles.Add("<none>");

            logger.LogWarning
                ("GetUsersPaginated: user {UserId} (roles: {CallerRoles}) denied — customers/read-only may not list users.",
                currentUser.UserId, string.Join(",", callerRoles));

            return new PaginatedResult<UserListItemDto>(Array.Empty<UserListItemDto>(), 0, 1, 1);
        }

        // ------------------------------------------------------------------
        // 1. Determine whether the caller may see GroupName.
        //    Admin/Manager: yes. Employee: no (GroupName is internal).
        // ------------------------------------------------------------------
        var canSeeGroup =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager);

        // ------------------------------------------------------------------
        // 2. Clamp page parameters.
        // ------------------------------------------------------------------
        var pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
        var pageSize = query.PageSize < 1
            ? 20
            : query.PageSize > MaxPageSize
                ? MaxPageSize
                : query.PageSize;

        // ------------------------------------------------------------------
        // 3. Load the page. The repository applies all filters server-side.
        // ------------------------------------------------------------------
        var paginated = await userRepository.GetPaginatedAsync
            (
            searchTerm: query.SearchTerm,
            isActive: query.IsActive,
            groupId: query.GroupId,
            pageNumber: pageNumber,
            pageSize: pageSize,
            cancellationToken: cancellationToken
            );

        // ------------------------------------------------------------------
        // 3b. Batch-resolve customer group names for the page of users.
        //     We load ALL groups (expected 5-15 rows) in a single round-trip
        //     and build an in-memory lookup. Same pattern as the category
        //     tree load in GetProductsPaginatedQueryHandler.
        //
        //     GroupName is exposed on the DTO ONLY for Admin/Manager callers
        //     (canSeeGroup below). For Employee callers, we still load the
        //     groups (cheap) but project to null — see step 4 below.
        // ------------------------------------------------------------------
        List<Domain.Customers.Entities.CustomerGroup>? groups = null;
        try
        {
            groups = await customerGroupRepository.GetAllAsync(includeInactive: true, cancellationToken);
        }
        catch (Exception ex)
        {
            // Non-fatal — if the group load fails we still return the users
            // with empty group-name fields. The UI renders an em-dash for
            // empty group names, which is acceptable.
            logger.LogWarning(ex,
                "GetUsersPaginated: failed to load customer groups for name enrichment. "
                + "Users will be returned without group names.");
            groups = new List<Domain.Customers.Entities.CustomerGroup>();
        }

        var groupById = groups.ToDictionary(g => g.Id);

        // ------------------------------------------------------------------
        // 4. Project to DTO. Strip GroupName if the caller can't see it.
        //    Gender is always visible — it's not security-sensitive.
        //
        //    ROLES (Phase 6.3): We also batch-load each user's ASP.NET
        //    Identity role names via GetRolesByUserIdsAsync. The AdminUsers
        //    page needs this to gate the activate/deactivate button: a
        //    Manager (non-Admin) may only act on users in the Employee role.
        //    Loading roles per-row would N+1 the database; the batched call
        //    is one extra round-trip for the whole page.
        // ------------------------------------------------------------------
        var userIds = paginated.Items.Select(u => u.Id).ToList();
        var rolesByUser = await userRepository.GetRolesByUserIdsAsync(userIds, cancellationToken);

        var dtos = paginated.Items
            .Select
            (
            u => new UserListItemDto
            {
                Id = u.Id,
                WorkerId = u.WorkerId,
                FullName = u.FullName,
                Gender = u.Gender,
                GroupId = u.GroupId,
                // Resolve the display name from the in-memory group lookup.
                // Null when the user has no group (staff) OR the caller
                // can't see group names OR the group was hard-deleted
                // (defensive — shouldn't happen since CustomerGroup uses
                // soft-delete via IsActive=false).
                GroupName = canSeeGroup && u.GroupId is not null && groupById.TryGetValue(u.GroupId.Value, out var g)
                    ? g.Name
                    : null,
                IsActive = u.IsActive,
                // Roles: empty list if the user has no roles (rare — only
                // happens if role seeding is incomplete). The dictionary
                // lookup is O(1); a missing key just means "no roles".
                Roles = rolesByUser.TryGetValue(u.Id, out var roles)
                    ? roles
                    : new List<string>()
            }
            )
            .ToList();

        return new PaginatedResult<UserListItemDto>(dtos, paginated.TotalCount, pageNumber, pageSize);
    }
}
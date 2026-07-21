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
            return new PaginatedResult<UserListItemDto>(
                Array.Empty<UserListItemDto>(), 0, 1, 1);
        }

        var canListUsers =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager) ||
            currentUser.IsInRole(Roles.Employee);

        if (!canListUsers)
        {
            logger.LogWarning
                ("GetUsersPaginated: user {UserId} (roles: {Roles}) denied — customers/read-only may not list users.",
                currentUser.UserId, string.Join(",", new[] { Roles.Admin, Roles.Manager, Roles.Employee, Roles.Customer, Roles.ReadOnly }));

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
            groupName: query.GroupName,
            pageNumber: pageNumber,
            pageSize: pageSize,
            cancellationToken: cancellationToken
            );

        // ------------------------------------------------------------------
        // 4. Project to DTO. Strip GroupName if the caller can't see it.
        // ------------------------------------------------------------------
        var dtos = paginated.Items
            .Select
            (
            u => new UserListItemDto
            {
                Id = u.Id,
                WorkerId = u.WorkerId,
                FullName = u.FullName,
                GroupName = canSeeGroup ? u.GroupName : null,
                IsActive = u.IsActive
            }
            )
            .ToList();

        return new PaginatedResult<UserListItemDto>(dtos, paginated.TotalCount, pageNumber, pageSize);
    }
}
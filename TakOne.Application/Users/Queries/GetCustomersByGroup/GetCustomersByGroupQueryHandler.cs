using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Users.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Queries.GetCustomersByGroup;

/// <summary>
/// Handler for <see cref="GetCustomersByGroupQuery"/>.
/// </summary>
public sealed class GetCustomersByGroupQueryHandler
{
    public static async Task<Result<List<UserListItemDto>>> HandleAsync
        (
        GetCustomersByGroupQuery query,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        ILogger<GetCustomersByGroupQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetCustomersByGroup: unauthenticated call rejected.");

            return Result<List<UserListItemDto>>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Authorization. Only Admin / Manager / Employee may list
        //    customers by group.
        // ------------------------------------------------------------------
        var canCall =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager) ||
            currentUser.IsInRole(Roles.Employee);

        if (!canCall)
        {
            logger.LogWarning
                ("GetCustomersByGroup: user {UserId} denied (customers/read-only may not list by group).",
                currentUser.UserId);

            return Result<List<UserListItemDto>>.Failure
                ("You do not have permission to view customers by group.");
        }

        // ------------------------------------------------------------------
        // 2. Validate the GroupName. We don't use a FluentValidation
        //    validator for queries (queries are read-only and the failure
        //    mode is benign), so we do the check inline.
        // ------------------------------------------------------------------
        if (string.IsNullOrWhiteSpace(query.GroupName))
        {
            logger.LogInformation
                ("GetCustomersByGroup: user {UserId} provided invalid group name.",
                currentUser.UserId);

            return Result<List<UserListItemDto>>.Failure("Group name is required.");
        }

        // ------------------------------------------------------------------
        // 3. Determine whether the caller may see GroupName on each row.
        //    Admin/Manager: yes. Employee: no.
        // ------------------------------------------------------------------
        var canSeeGroup =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager);

        // ------------------------------------------------------------------
        // 4. Load the users in this group.
        // ------------------------------------------------------------------
        var users = await userRepository.GetByGroupNameAsync(query.GroupName, cancellationToken);

        // ------------------------------------------------------------------
        // 5. Project to DTO. Sort by FullName for stable UI rendering.
        //    Strip GroupName if the caller can't see it. (The whole list
        //    is the same group, so this is more about not leaking the
        //    field through the wire format than about per-row variance.)
        // ------------------------------------------------------------------
        var dtos = users
            .OrderBy(u => u.FullName, StringComparer.OrdinalIgnoreCase)
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

        return Result<List<UserListItemDto>>.Success(dtos);
    }
}
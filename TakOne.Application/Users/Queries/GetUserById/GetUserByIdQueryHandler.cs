using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Users.DTOs;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Queries.GetUserById;

/// <summary>
/// Handler for <see cref="GetUserByIdQuery"/>.
///
/// The handler ALSO loads the user's ASP.NET Identity roles via
/// <see cref="IUserAccountService.GetRolesAsync"/>. Email is fetched from the
/// same service (TODO in Infrastructure — see remarks on
/// <see cref="IUserAccountService"/>). For now, Email is left null on the
/// DTO; the Infrastructure implementation can extend IUserAccountService
/// with a GetEmailAsync method or fetch it as part of a richer DTO.
/// </summary>
public sealed class GetUserByIdQueryHandler
{
    public static async Task<Result<UserDto>> HandleAsync
        (
        GetUserByIdQuery query,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUserAccountService userAccountService,
        ILogger<GetUserByIdQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("GetUserById: unauthenticated call rejected.");

            return Result<UserDto>.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Authorization. Customers/ReadOnly may only view their own
        //    profile. Admin/Manager/Employee may view anyone.
        //
        //    On failure, return a generic "not found" message — never leak
        //    that the user exists but the caller can't see them.
        // ------------------------------------------------------------------
        var canViewAnyUser =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager) ||
            currentUser.IsInRole(Roles.Employee);

        if (!canViewAnyUser && query.UserId != currentUser.UserId)
        {
            logger.LogWarning
                ("GetUserById: user {ActorId} denied access to user {TargetId}.",
                currentUser.UserId, query.UserId);

            return Result<UserDto>.Failure($"User '{query.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 2. Load the user from the repository.
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(query.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogInformation
                ("GetUserById: user {UserId} not found. Requested by user {ActorId}.",
                query.UserId, currentUser.UserId);

            return Result<UserDto>.Failure($"User '{query.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 3. Load ASP.NET Identity roles. This is a separate call to
        //    IUserAccountService because roles live on ApplicationUser, not
        //    on the Domain User. In Infrastructure, this resolves to
        //    UserManager.GetRolesAsync — one DB round-trip.
        // ------------------------------------------------------------------
        var roles = await userAccountService.GetRolesAsync(user.Id, cancellationToken);

        // ------------------------------------------------------------------
        // 4. Determine whether the caller may see the user's GroupName.
        //    - Admin and Manager may see GroupName for ANY user.
        //    - Employee may see GroupName ONLY for users in the Customer
        //      role — that's the only target an Employee is allowed to
        //      manage the group of (per the role-scope rules enforced
        //      server-side by AssignUserToGroupCommandHandler). Hiding
        //      GroupName for non-Customer targets from Employee viewers
        //      is defense-in-depth: the Employee can't act on those
        //      targets anyway, so they don't need to see the group.
        //    - Customers viewing their own profile never see their own
        //      group (customers don't manage groups — they just live in
        //      one for purchase-limit purposes).
        // ------------------------------------------------------------------
        var canSeeGroup =
            currentUser.IsInRole(Roles.Admin) ||
            currentUser.IsInRole(Roles.Manager);

        if (!canSeeGroup && currentUser.IsInRole(Roles.Employee))
        {
            // Employee viewer: only allowed to see the group of users whose
            // roles contain Customer. (A Customer+Employee target is still
            // treated as "another employee" by the AssignUserToGroup handler
            // — but for visibility we use the simpler "has Customer role"
            // check, which is permissive enough to let the Employee see the
            // current group of any user they might be navigating to.)
            canSeeGroup = roles.Contains(Roles.Customer);
        }

        // ------------------------------------------------------------------
        // 5. Project to DTO. Email is left null for now — see class-level
        //    remark. Roles come from IUserAccountService. Gender is sourced
        //    from the Domain User (the source of truth — ApplicationUser's
        //    copy is denormalized for the admin list view).
        // ------------------------------------------------------------------
        var dto = new UserDto
        {
            Id = user.Id,
            WorkerId = user.WorkerId,
            FullName = user.FullName,
            Gender = user.Gender,
            GroupName = canSeeGroup ? user.GroupName : null,
            IsActive = user.IsActive,
            Email = null, // TODO: extend IUserAccountService with GetEmailAsync
            Roles = roles.ToList()
        };

        return Result<UserDto>.Success(dto);
    }
}
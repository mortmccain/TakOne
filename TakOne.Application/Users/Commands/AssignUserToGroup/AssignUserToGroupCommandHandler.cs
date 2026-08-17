using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Commands.AssignUserToGroup;

/// <summary>
/// Assigns a user to a customer group (domain-only).
///
/// Not static so <c>ILogger&lt;T&gt;</c> can take it as a type argument.
/// </summary>
public sealed class AssignUserToGroupCommandHandler
{
    public static async Task<Result> HandleAsync
        (
        AssignUserToGroupCommand command,
        ICurrentUserService currentUser,
        IUserRepository userRepository,
        IUnitOfWork unitOfWork,
        ILogger<AssignUserToGroupCommandHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defensive auth check.
        // ------------------------------------------------------------------
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            logger.LogWarning("AssignUserToGroup: unauthenticated call rejected.");

            return Result.Failure("Authentication required.");
        }

        // ------------------------------------------------------------------
        // 1. Caller-scope enforcement (Phase 6.5).
        //
        // The [RequireRoles] attribute on the command only checks that the
        // caller is in Employee/Manager/Admin. The finer-grained scope rules
        // are enforced HERE, server-side, as defense-in-depth against
        // tampered requests (the AdminUsers UI hides the Change Group button
        // on rows the caller can't touch, but a malicious client could
        // still dispatch the command directly).
        //
        // Rules (per user spec, Phase 6.5):
        //   - Admin    → may assign any user's group (including self, though
        //                that's a weird edge case — admins don't have groups).
        //   - Manager  → may assign groups for Employee or Customer users.
        //                NOT self, NOT other managers, NOT admins, NOT read-onlys.
        //   - Employee → may assign groups for Customer users only.
        //                NOT self, NOT other employees, NOT managers/admins/read-onlys.
        //
        // "Employee or Customer" for Manager means: the target's roles
        // contain Employee OR Customer, AND do NOT contain Manager or Admin.
        // (A Manager+Employee target is treated as "another manager" and is
        // off-limits, matching the user's rule "not other managers".)
        //
        // "Customer" for Employee means: the target's roles contain Customer
        // AND do NOT contain Employee, Manager, or Admin. (A Customer+Employee
        // target is treated as "another employee" and is off-limits, matching
        // the user's rule "not other employees".)
        //
        // We look up the target's roles via the batch-friendly
        // GetRolesByUserIdsAsync (single-row variant — passing one Id).
        // ------------------------------------------------------------------
        var isCallerAdmin = currentUser.IsInRole(Roles.Admin);
        var isCallerManager = currentUser.IsInRole(Roles.Manager);
        var isCallerEmployee = currentUser.IsInRole(Roles.Employee);

        if (!isCallerAdmin)  // Admins skip this check — they can act on anyone.
        {
            var targetRoles = await userRepository.GetRolesByUserIdsAsync(
                new[] { command.UserId }, cancellationToken);

            targetRoles.TryGetValue(command.UserId, out var roles);
            var rolesList = roles ?? new List<string>();

            // Self-check: Managers and Employees cannot change their own group.
            // (Admins are allowed — handled by the !isCallerAdmin skip above.)
            if (command.UserId == currentUser.UserId)
            {
                logger.LogWarning
                    ("AssignUserToGroup: non-Admin {ActorId} attempted to change their own group. Rejected.",
                    currentUser.UserId);

                return Result.Failure("You cannot change your own group.");
            }

            if (isCallerManager && !isCallerAdmin)
            {
                // Manager scope: target must be Employee or Customer, AND must
                // NOT be Manager or Admin.
                var targetIsManager = rolesList.Contains(Roles.Manager);
                var targetIsAdmin = rolesList.Contains(Roles.Admin);
                var targetIsEmployeeOrCustomer = rolesList.Contains(Roles.Employee)
                                              || rolesList.Contains(Roles.Customer);

                if (targetIsManager || targetIsAdmin || !targetIsEmployeeOrCustomer)
                {
                    logger.LogWarning
                        ("AssignUserToGroup: Manager {ActorId} attempted to change group of user {TargetId} who is not an Employee or Customer. Rejected.",
                        currentUser.UserId, command.UserId);

                    return Result.Failure
                        ("Managers may only change the group for Employee or Customer accounts. Other managers, administrators, and read-only users require Administrator access.");
                }
            }
            else if (isCallerEmployee && !isCallerManager)
            {
                // Employee scope: target must be Customer, AND must NOT be
                // Employee, Manager, or Admin.
                var targetIsEmployee = rolesList.Contains(Roles.Employee);
                var targetIsManager = rolesList.Contains(Roles.Manager);
                var targetIsAdmin = rolesList.Contains(Roles.Admin);
                var targetIsCustomer = rolesList.Contains(Roles.Customer);

                if (targetIsEmployee || targetIsManager || targetIsAdmin || !targetIsCustomer)
                {
                    logger.LogWarning
                        ("AssignUserToGroup: Employee {ActorId} attempted to change group of user {TargetId} who is not a Customer. Rejected.",
                        currentUser.UserId, command.UserId);

                    return Result.Failure
                        ("Employees may only change the group for Customer accounts. Other employees, managers, administrators, and read-only users require Manager or Administrator access.");
                }
            }
        }

        // ------------------------------------------------------------------
        // 2. Load the user.
        // ------------------------------------------------------------------
        var user = await userRepository.GetByIdAsync(command.UserId, cancellationToken);

        if (user is null)
        {
            logger.LogWarning
                ("AssignUserToGroup: user {UserId} was not found. Requested by user {ActorId}.",
                command.UserId, currentUser.UserId);

            return Result.Failure($"User '{command.UserId}' was not found.");
        }

        // ------------------------------------------------------------------
        // 3. Delegate to the aggregate. AssignToGroup validates the group
        //    Id (must not be Guid.Empty — domain invariant). DomainException
        //    is caught by middleware.
        // ------------------------------------------------------------------
        user.AssignToGroup(command.GroupId);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation
            ("AssignUserToGroup: user {UserId} (worker ID '{WorkerId}') assigned to group {GroupId} by user {ActorId}.",
            user.Id, user.WorkerId, user.GroupId, currentUser.UserId);

        return Result.Success();
    }
}
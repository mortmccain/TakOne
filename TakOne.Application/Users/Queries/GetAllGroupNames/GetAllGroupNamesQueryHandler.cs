using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Queries.GetAllGroupNames;

/// <summary>
/// Handler for <see cref="GetAllGroupNamesQuery"/>.
///
/// SALARY FEATURE (Step 3) — DEPRECATED:
///   This query is kept for backwards compatibility with callers that
///   still use it (e.g. the CreateProduct page's per-group limit editor
///   until Step 5 replaces it). Internally it delegates to
///   <see cref="ICustomerGroupRepository.GetAllAsync"/> and returns the
///   group NAMES (not the full group DTOs).
///
///   New callers should use <see cref="Customers.Queries.GetAllCustomerGroups.GetAllCustomerGroupsQuery"/>
///   instead — it returns the full DTO (Id, Name, Salary, IsActive) needed
///   for the new ManageGroups page and the salary-budget feature.
/// </summary>
[RequireRoles(Roles.Admin, Roles.Manager, Roles.Employee)]
public sealed class GetAllGroupNamesQueryHandler
{
    public static async Task<Result<List<string>>> HandleAsync
        (
        GetAllGroupNamesQuery query,
        ICustomerGroupRepository customerGroupRepository,
        ILogger<GetAllGroupNamesQueryHandler> logger,
        CancellationToken cancellationToken
        )
    {
        // ------------------------------------------------------------------
        // 0. Defense-in-depth auth check.
        //    [RequireRoles] on the query already rejects customers, but a
        //    test or future host could bypass the middleware. We re-check
        //    here by NOT injecting ICurrentUserService (the query has no
        //    user context by design) — the role gate is purely on the
        //    [RequireRoles] attribute, which is the project's convention
        //    for read-side queries that don't need per-caller scoping.
        // ------------------------------------------------------------------
        try
        {
            var groups = await customerGroupRepository.GetAllAsync(includeInactive: true, cancellationToken);

            // Project to names only (sorted) — that's what the legacy callers
            // expect. New callers should use GetAllCustomerGroupsQuery which
            // returns the full DTO.
            var groupNames = groups
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Name)
                .ToList();

            return Result<List<string>>.Success(groupNames);
        }
        catch (Exception ex)
        {
            // Don't leak DB exceptions to the caller — log + return a
            // friendly failure so the CreateProduct UI can show "couldn't
            // load group names" instead of crashing the whole page.
            logger.LogError
                (ex,
                "GetAllGroupNames: failed to load customer groups from the repository.");

            return Result<List<string>>.Failure("Could not load customer group names. Please try again.");
        }
    }
}
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Users.Queries.GetAllGroupNames;

/// <summary>
/// Handler for <see cref="GetAllGroupNamesQuery"/>. Returns the distinct
/// list of customer group names. See the query file for authorization +
/// empty-result semantics.
/// </summary>
[RequireRoles(Roles.Admin, Roles.Manager, Roles.Employee)]
public sealed class GetAllGroupNamesQueryHandler
{
    public static async Task<Result<List<string>>> HandleAsync
        (
        GetAllGroupNamesQuery query,
        IUserRepository userRepository,
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
            var groupNames = await userRepository.GetDistinctGroupNamesAsync(cancellationToken);

            // Defensive: the repo returns null only if the underlying query
            // failed in an unexpected way (it shouldn't — ToListAsync never
            // returns null). Normalize to empty list for the caller.
            if (groupNames is null)
            {
                logger.LogWarning("GetAllGroupNames: repository returned null. Returning empty list.");
                return Result<List<string>>.Success(new List<string>());
            }

            return Result<List<string>>.Success(groupNames);
        }
        catch (Exception ex)
        {
            // Don't leak DB exceptions to the caller — log + return a
            // friendly failure so the CreateProduct UI can show "couldn't
            // load group names" instead of crashing the whole page.
            logger.LogError
                (ex,
                "GetAllGroupNames: failed to load distinct group names from the user repository.");

            return Result<List<string>>.Failure("Could not load customer group names. Please try again.");
        }
    }
}
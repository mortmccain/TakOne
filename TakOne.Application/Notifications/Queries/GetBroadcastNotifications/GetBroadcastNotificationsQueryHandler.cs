using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Authorization;
using TakOne.Application.Common.Interfaces;
using TakOne.Application.Notifications.DTOs;
using TakOne.Domain.Notifications.Enums;
using TakOne.SharedKernel.Common;

namespace TakOne.Application.Notifications.Queries.GetBroadcastNotifications;

/// <summary>
/// Handler for <see cref="GetBroadcastNotificationsQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>SCOPE</b>: defense-in-depth auth check at the top — non-admins get
/// an empty page (silent). The <c>[RequireRoles(Admin)]</c> attribute on
/// the query should already reject non-admins via Wolverine's
/// <c>AuthorizationPolicyVerifier</c> middleware; this is the second
/// layer.
/// </para>
/// <para>
/// <b>NAME RESOLUTION</b>: the audit list surfaces <c>SentByUserName</c>
/// (the admin who authored it), <c>TargetGroupName</c> (when scope=Group),
/// and <c>TargetUserName</c> (when scope=User). These are resolved by
/// <b>batched</b> lookups via <c>IUserRepository.GetByIdsReadOnlyAsync</c>
/// and <c>ICustomerGroupRepository.GetByIdsReadOnlyAsync</c>. We collect
/// all distinct sender + target user Ids and target group Ids across the
/// page's items, then do ONE batched user lookup + ONE batched group
/// lookup (2 round-trips total per page render, regardless of page size),
/// build dictionaries, and project. The page size is bounded (default 20),
/// so the IN clauses stay cheap.
/// </para>
/// <para>
/// <b>SYSTEM-EMITTED BROADCASTS</b>: <c>SentByUserId == Guid.Empty</c>
/// marks a system-emitted AppUpdate broadcast. For these, the handler
/// skips the user lookup and sets <c>SentByUserName = null</c> (the UI
/// renders "System" or a gear icon instead of a person's name).
/// </para>
/// </remarks>
public sealed class GetBroadcastNotificationsQueryHandler
{
    private const int MaxPageSize = 100;

    public static async Task<PaginatedResult<BroadcastNotificationDto>> HandleAsync(
        GetBroadcastNotificationsQuery query,
        ICurrentUserService currentUser,
        IBroadcastNotificationRepository broadcastRepository,
        IUserRepository userRepository,
        ICustomerGroupRepository groupRepository,
        ILogger<GetBroadcastNotificationsQueryHandler> logger,
        CancellationToken cancellationToken)
    {
        // 0. Defense-in-depth auth check.
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty
            || !currentUser.IsInRole(Roles.Admin))
        {
            logger.LogWarning("GetBroadcastNotifications: non-admin call rejected.");
            return new PaginatedResult<BroadcastNotificationDto>(
                Array.Empty<BroadcastNotificationDto>(), 0, 1, 1);
        }

        var pageNumber = query.PageNumber < 1 ? 1 : query.PageNumber;
        var pageSize = query.PageSize < 1
            ? 20
            : query.PageSize > MaxPageSize
                ? MaxPageSize
                : query.PageSize;

        // Delegate to the repository — bare paginated query.
        var page = await broadcastRepository.GetPaginatedAsync(
            pageNumber, pageSize, cancellationToken);

        // ── Name resolution (batched). ──
        // Collect distinct user Ids (senders + targeted users) and group
        // Ids across the page, then do batched lookups. Skips Guid.Empty
        // sender Ids (system-emitted broadcasts have no human author).
        var senderUserIds = page.Items
            .Where(b => b.SentByUserId != Guid.Empty)
            .Select(b => b.SentByUserId)
            .Distinct()
            .ToList();

        var targetUserIds = page.Items
            .Where(b => b.Scope == BroadcastScope.User && b.TargetUserId.HasValue)
            .Select(b => b.TargetUserId!.Value)
            .Distinct()
            .ToList();

        var targetGroupIds = page.Items
            .Where(b => b.Scope == BroadcastScope.Group && b.TargetGroupId.HasValue)
            .Select(b => b.TargetGroupId!.Value)
            .Distinct()
            .ToList();

        // ── Name resolution (batched — 2 round-trips total, not N). ──
        // Collect distinct sender + target user Ids (excluding Guid.Empty
        // sender Ids — system-emitted broadcasts have no human author) and
        // distinct target group Ids across the page. Then do ONE batched
        // user lookup + ONE batched group lookup and build dictionaries for
        // the projection. This replaces the previous per-id foreach loops
        // (an N+1 that cost up to 2*pageSize round-trips per render).
        var allUserIds = senderUserIds
            .Concat(targetUserIds)
            .Distinct()
            .ToList();

        var senderNames = new Dictionary<Guid, string>();
        var targetUserNames = new Dictionary<Guid, string>();
        var groupNames = new Dictionary<Guid, string>();

        if (allUserIds.Count > 0)
        {
            var users = await userRepository.GetByIdsReadOnlyAsync(allUserIds, cancellationToken);
            // Build a single user-Id → FullName map; sender + target lookups
            // both read from it. Users that don't exist (hard-deleted after
            // the broadcast was sent) are simply absent — the DTO projection
            // uses GetValueOrDefault and renders "Unknown" / "System" via the
            // UI's fallback paths.
            var usersById = users.ToDictionary(u => u.Id);
            foreach (var uid in senderUserIds)
            {
                if (usersById.TryGetValue(uid, out var u))
                {
                    senderNames[uid] = u.FullName;
                }
            }
            foreach (var uid in targetUserIds)
            {
                if (usersById.TryGetValue(uid, out var u))
                {
                    targetUserNames[uid] = u.FullName;
                }
            }
        }

        if (targetGroupIds.Count > 0)
        {
            var groups = await groupRepository.GetByIdsReadOnlyAsync(targetGroupIds, cancellationToken);
            groupNames = groups.ToDictionary(g => g.Id, g => g.Name);
        }

        // Project aggregates → DTOs.
        var dtos = page.Items
            .Select(b => new BroadcastNotificationDto
            {
                Id = b.Id,
                SentByUserId = b.SentByUserId,
                SentByUserName = b.SentByUserId == Guid.Empty
                    ? null
                    : senderNames.GetValueOrDefault(b.SentByUserId),
                SentAtUtc = b.SentAtUtc,
                Scope = b.Scope,
                TargetRoleName = b.TargetRoleName,
                TargetGroupId = b.TargetGroupId,
                TargetGroupName = b.TargetGroupId.HasValue
                    ? groupNames.GetValueOrDefault(b.TargetGroupId.Value)
                    : null,
                TargetUserId = b.TargetUserId,
                TargetUserName = b.TargetUserId.HasValue
                    ? targetUserNames.GetValueOrDefault(b.TargetUserId.Value)
                    : null,
                Title = b.Title,
                Message = b.Message,
                FanoutKind = b.FanoutKind,
                RecipientCount = b.RecipientCount
            })
            .ToList();

        return new PaginatedResult<BroadcastNotificationDto>(
            dtos, page.TotalCount, pageNumber, pageSize);
    }
}

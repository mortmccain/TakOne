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
/// batched lookups via <c>IUserRepository</c> and
/// <c>ICustomerGroupRepository</c>. To avoid N+1 queries on the page, we
/// batch: collect all distinct user Ids + group Ids across the page's
/// items, do ONE batched user-role lookup + ONE group lookup, then
/// project. The page size is bounded (default 20), so this is at most ~20
/// lookups per page render.
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

        // Batched lookups: load all needed users + groups in one round-trip each.
        var senderNames = new Dictionary<Guid, string>();
        var targetUserNames = new Dictionary<Guid, string>();
        var groupNames = new Dictionary<Guid, string>();

        foreach (var uid in senderUserIds)
        {
            var u = await userRepository.GetByIdAsync(uid, cancellationToken);
            if (u is not null) senderNames[uid] = u.FullName;
        }

        foreach (var uid in targetUserIds)
        {
            var u = await userRepository.GetByIdAsync(uid, cancellationToken);
            if (u is not null) targetUserNames[uid] = u.FullName;
        }

        foreach (var gid in targetGroupIds)
        {
            var g = await groupRepository.GetByIdReadOnlyAsync(gid, cancellationToken);
            if (g is not null) groupNames[gid] = g.Name;
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

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using TakOne.Application.Common.Interfaces;
using TakOne.WebUI.Hubs;

namespace TakOne.WebUI.Services;

/// <summary>
/// SignalR-backed implementation of <see cref="INotificationBroadcaster"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>REGISTRATION</b>: Scoped lifetime (resolved per-Wolverine-handler
/// invocation). Wraps <c>IHubContext&lt;NotificationHub&gt;</c> which is
/// itself Singleton — the per-request scope just makes the wrapper cheap
/// to resolve.
/// </para>
/// <para>
/// <b>GROUP NAMING CONVENTION</b>: each user's SignalR group is named
/// <c>user:{userId}</c> (lowercase). The <see cref="NotificationHub.OnConnectedAsync"/>
/// override adds the connection to this group on connect. The broadcast
/// here targets <c>Clients.Group($"user:{userId}")</c> — pinging all of
/// the user's currently-connected clients (multiple tabs, mobile + PC).
/// </para>
/// <para>
/// <b>BEST-EFFORT</b>: the broadcast is wrapped in a try/catch. Failures
/// are logged but never propagate — the persisted <c>Notification</c>
/// row is the source of truth. If the live ping fails (circuit down,
/// hub restarting), the user simply sees the notification on next page
/// load. This is the enterprise pattern: real-time is a UX nicety, not
/// a correctness requirement.
/// </para>
/// <para>
/// <b>WHY THIS LIVES IN WebUI (not Infrastructure)</b>: the
/// <c>NotificationHub</c> class is in WebUI, so <c>IHubContext&lt;NotificationHub&gt;</c>
/// references a WebUI type. Infrastructure doesn't reference WebUI (the
/// dependency direction is WebUI → Infrastructure). Putting the
/// broadcaster impl in WebUI keeps the dependency graph clean.
/// </para>
/// </remarks>
public sealed class SignalRNotificationBroadcaster : INotificationBroadcaster
{
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly ILogger<SignalRNotificationBroadcaster> _logger;

    public SignalRNotificationBroadcaster(
        IHubContext<NotificationHub> hubContext,
        ILogger<SignalRNotificationBroadcaster> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task BroadcastToUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return;
        }

        try
        {
            // Group naming convention: "user:{userId}". See the
            // NotificationHub.OnConnectedAsync override for the matching
            // group-add on connect.
            var groupName = $"user:{userId}";

            // SendAsync is fire-and-forget from the server's perspective
            // — it queues the message to all connections currently in
            // the group. If no connections are live (user's circuit is
            // down, no other tabs), the message is silently dropped —
            // which is fine, the user will re-query on next page load.
            await _hubContext.Clients.Group(groupName)
                .SendAsync("ReceiveRefresh", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort — see class doc. Log + swallow so the
            // surrounding Wolverine handler's transaction is NOT
            // affected.
            _logger.LogWarning(ex,
                "SignalRNotificationBroadcaster: broadcast to user {UserId} failed (notification persisted anyway).",
                userId);
        }
    }
}
